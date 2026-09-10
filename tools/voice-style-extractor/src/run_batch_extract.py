"""Batched style-vector extraction for many speakers at once.

Each speaker's style vector only reaches its own output, so stacking B speakers
into one forward pass and summing their per-speaker losses gives every vector
exactly the gradient it would have received alone. Measured on an RTX 3090:
~5x at B=8, ~6.7x at B=16, and 16 is the practical maximum on 24 GB.

Two correctness rules the batching has to respect:

  * A speaker that reaches the threshold stops being updated immediately. Left
    in the batch it would keep descending past the threshold and drift into the
    region the TTS was never trained on.
  * The loss is SUMMED over the batch, not averaged, so the effective learning
    rate per speaker matches a solo run.

Usage:
  python src/run_batch_extract.py --speakers seedtts --limit 6
  python src/run_batch_extract.py --speakers vctk --batch 16 --save-wav
  python src/run_batch_extract.py --speakers vctk --threshold 0.24 \
                                  --resume-from results/vctk_layer4
"""
import argparse
import json
import os
import time

import librosa
import numpy as np
import soundfile as sf
import torch

from core import (LAYER, LR, SPEED, THRESHOLD, TOTAL_STEP,
                  build_prompts, load_pipeline, load_style_tensors, load_wavlm,
                  preset_paths, preset_tensors, save_style, style_loss,
                  target_stats_from_file, to16k, tts_forward, wav_stats)


def _seedtts():
    """The 44 Common Voice references from Seed-TTS Eval; ids are 01..44."""
    import glob
    out = [{"id": os.path.basename(p)[:2], "wav": p, "corpus": "seedtts"}
           for p in sorted(glob.glob("wavs/[0-9][0-9].wav"))]
    mp = "results/_layer3_seedtts/opt_extended24/speaker_mapping.json"
    if os.path.exists(mp):
        for m in json.load(open(mp, encoding="utf-8")):
            w = m["wav_path"].replace("\\", "/")
            if os.path.exists(w):
                out.append({"id": str(m["id"]).zfill(2), "wav": w,
                            "corpus": "seedtts"})
    return out


def _mine():
    """wavs/ 에 직접 넣은 목소리. 파일 이름이 그대로 결과 JSON 이름이 된다.

    원본 저장소에는 이 소스가 없다. seedtts 소스가 wavs/01.wav 처럼 두 자리 숫자만
    긁어가서, 내 목소리를 넣으려면 파일 이름을 숫자로 바꿔야 했다. 화자가 함장/전술/기관
    이면 결과 JSON도 그 이름으로 나오는 편이 낫다.
    """
    import glob
    return [{"id": os.path.splitext(os.path.basename(p))[0], "wav": p, "corpus": "mine"}
            for p in sorted(glob.glob("wavs/*.wav"))]


def _vctk():
    """VCTK speakers chosen by prepare_vctk.py; ids are pNNN, so no collision."""
    sel = "data/vctk_speakers.json"
    if not os.path.exists(sel):
        raise SystemExit("run src/prepare_vctk.py --export first")
    return [{"id": c["spk"], "wav": c.get("ref_wav") or c["path"],
             "gender": c.get("gender"), "accent": c.get("accent"),
             "corpus": "vctk"}
            for c in json.load(open(sel, encoding="utf-8"))]


def load_speakers(kind, limit):
    """-> [{'id', 'wav', ...}] ; the id doubles as the output filename.

    `all` runs both corpora in one pass, which is worth doing: they differ in
    recording condition (VCTK is studio, Seed-TTS is crowdsourced Common Voice),
    and VCTK carries real gender metadata while the Seed-TTS ids overlap with
    the earlier layer-3 run.
    """
    sources = {"seedtts": _seedtts, "vctk": _vctk, "mine": _mine}
    if kind == "all":
        out = _vctk() + _seedtts()
    elif kind in sources:
        out = sources[kind]()
    else:
        raise SystemExit(f"unknown --speakers {kind}")
    return out[:limit] if limit else out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--speakers", default="mine",
                    choices=["all", "seedtts", "vctk", "mine"])
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--layer", type=int, default=LAYER)
    ap.add_argument("--threshold", type=float, default=THRESHOLD)
    ap.add_argument("--batch", type=int, default=8)
    ap.add_argument("--max-steps", type=int, default=3000)
    ap.add_argument("--plateau", type=int, default=1500,
                    help="give up on a speaker after this many steps with no gain")
    ap.add_argument("--out", default="results/batch_run")
    ap.add_argument("--save-wav", action="store_true",
                    help="also write the reference and the 5 synthesized prompts")
    ap.add_argument("--resume-from", default=None,
                    help="continue from a previous run's styles/ rather than from "
                         "a preset; pair with a lower --threshold")
    ap.add_argument("--lr", type=float, default=LR)
    args = ap.parse_args()

    os.makedirs(f"{args.out}/styles", exist_ok=True)
    print(f"layer {args.layer}   threshold {args.threshold}   batch {args.batch}\n")

    models = load_pipeline()
    tts, dp, te, ve, voc = models
    wavlm = load_wavlm()
    prompts = build_prompts(tts, dp)

    speakers = load_speakers(args.speakers, args.limit)

    # ---- resume: start from a previous run's vectors ---------------------
    if args.resume_from:
        prev = json.load(open(f"{args.resume_from}/summary.json", encoding="utf-8"))
        if prev["layer"] != args.layer:
            raise SystemExit(
                f"previous run used layer {prev['layer']}, this one asks for "
                f"{args.layer}; the loss scales differ, so resuming is invalid")
        if args.threshold >= prev["threshold"]:
            print(f"note: previous threshold was {prev['threshold']}, this one is "
                  f"{args.threshold} -- nothing further to optimize\n")
        by_id = {d["id"]: d for d in prev["speakers"]}
        kept = []
        for s in speakers:
            p = f"{args.resume_from}/styles/{s['id']}.json"
            if not os.path.exists(p):
                continue
            s["resume_ttl"], s["resume_dp"] = load_style_tensors(p)
            rec = by_id.get(s["id"], {})
            s["prior_steps"] = rec.get("steps", 0)
            s["preset"] = f"models/voice_styles/{rec.get('preset', 'M1')}.json"
            s["init_dist"] = rec.get("init_dist", float("nan"))
            hp = f"{args.resume_from}/{s['id']}_loss.json"
            s["prior_hist"] = json.load(open(hp)) if os.path.exists(hp) else []
            kept.append(s)
        speakers = kept
        print(f"resuming {len(speakers)} speakers from {args.resume_from} "
              f"(threshold {prev['threshold']} -> {args.threshold})")

    print(f"speakers: {len(speakers)}\n")

    # ---- target statistics ----------------------------------------------
    for s in speakers:
        s["tgt"] = target_stats_from_file(wavlm, s["wav"], args.layer)

    # ---- nearest preset (skipped when resuming) --------------------------
    if not args.resume_from:
        presets = preset_paths()
        pfeat = {}
        for p in presets:
            ttl, sdp = preset_tensors(p)
            wav = tts_forward(prompts[0]["ids"], prompts[0]["msk"], ttl, sdp,
                              dp, te, ve, voc, TOTAL_STEP, SPEED,
                              prompts[0]["lat"], prompts[0]["lmask"])[0]
            with torch.no_grad():
                pfeat[p] = wav_stats(wavlm, to16k(wav.reshape(1, -1)), args.layer)
        for s in speakers:
            best, bd = None, float("inf")
            for p in presets:
                d = style_loss(pfeat[p], s["tgt"]).item()
                if d < bd:
                    bd, best = d, p
            s["preset"], s["init_dist"] = best, bd
        print("preset selection done ("
              + ", ".join(os.path.basename(s["preset"])[:2] for s in speakers[:8])
              + (" ..." if len(speakers) > 8 else "") + ")\n")

    # ---- batched optimization -------------------------------------------
    queue, active, done = list(speakers), [], []
    t_start = time.time()

    def admit():
        while len(active) < args.batch and queue:
            s = queue.pop(0)
            if "resume_ttl" in s:
                s["ttl"] = s.pop("resume_ttl").clone().requires_grad_(True)
                s["dp"] = s.pop("resume_dp")
            else:
                ttl, sdp = preset_tensors(s["preset"])
                s["ttl"] = ttl.clone().requires_grad_(True)
                s["dp"] = sdp
            s["opt"] = torch.optim.Adam([s["ttl"]], lr=args.lr)
            s["sched"] = torch.optim.lr_scheduler.ReduceLROnPlateau(
                s["opt"], patience=200, factor=0.5, min_lr=args.lr * 0.01)
            s["best"], s["best_ttl"], s["best_step"], s["step"] = 1e9, None, 0, 0
            s["hist"] = list(s.get("prior_hist", []))
            active.append(s)

    admit()
    global_step = 0
    while active:
        # One prompt per step for the whole batch, so the batch stays
        # rectangular. A global counter keeps the rotation smooth when a
        # finished speaker is replaced mid-run.
        pr = prompts[global_step % len(prompts)]
        global_step += 1
        b = len(active)
        ids = pr["ids"].expand(b, -1).contiguous()
        msk = pr["msk"].expand(b, -1, -1).contiguous()
        lat = pr["lat"].expand(b, -1, -1).contiguous()
        lmask = pr["lmask"].expand(b, -1, -1).contiguous()
        sttl = torch.cat([s["ttl"] for s in active], dim=0)
        sdp = torch.cat([s["dp"] for s in active], dim=0)

        wav, _ = tts_forward(ids, msk, sttl, sdp, dp, te, ve, voc,
                             TOTAL_STEP, SPEED, lat, lmask)
        gm, gs = wav_stats(wavlm, to16k(wav), args.layer)

        # per-speaker loss, SUMMED: each vector then receives its own gradient
        # at the scale it would have had running alone
        losses = [style_loss((gm[i], gs[i]),
                             (active[i]["tgt"][0][0], active[i]["tgt"][1][0]))
                  for i in range(b)]
        for s in active:
            s["opt"].zero_grad()
        torch.stack(losses).sum().backward()

        finished = []
        for i, s in enumerate(active):
            lv = losses[i].item()
            s["hist"].append(lv)
            if lv < s["best"] - 1e-6:
                s["best"], s["best_step"] = lv, s["step"]
                s["best_ttl"] = s["ttl"].detach().clone()
            torch.nn.utils.clip_grad_norm_([s["ttl"]], 1.0)
            s["opt"].step()
            s["sched"].step(lv)
            s["step"] += 1
            if (s["best"] <= args.threshold
                    or s["step"] - s["best_step"] >= args.plateau
                    or s["step"] >= args.max_steps):
                finished.append(s)

        for s in finished:
            active.remove(s)
            s["converged"] = bool(s["best"] <= args.threshold)
            sid, prior = s["id"], s.get("prior_steps", 0)
            out_json = f"{args.out}/styles/{sid}.json"
            save_style(out_json, s["best_ttl"], s["dp"], source_file=s["wav"])
            done.append({
                "id": sid, "wav": s["wav"],
                "preset": os.path.basename(s["preset"]).replace(".json", ""),
                "init_dist": round(s["init_dist"], 4),
                "best_loss": round(s["best"], 4),
                "steps": prior + s["best_step"] + 1,
                **({"added_steps": s["best_step"] + 1} if prior else {}),
                "converged": s["converged"], "style_json": out_json,
                **({"corpus": s["corpus"]} if "corpus" in s else {}),
                **({"gender": s["gender"]} if "gender" in s else {}),
                **({"accent": s["accent"]} if "accent" in s else {}),
            })
            with open(f"{args.out}/{sid}_loss.json", "w") as f:
                json.dump(s["hist"], f)
            added = f" (+{s['best_step'] + 1})" if prior else ""
            state = "converged" if s["converged"] else "PLATEAU  "
            print(f"  [{len(done):>3}/{len(speakers)}] {sid:<8} "
                  f"loss {s['best']:.4f} @ {prior + s['best_step'] + 1:>5} steps"
                  f"{added}  {state}  -> {out_json}   "
                  f"({(time.time() - t_start) / 60:.1f} min elapsed)", flush=True)

            if args.save_wav:
                d = f"{args.out}/wav/{sid}"
                os.makedirs(d, exist_ok=True)
                # the reference sits beside the synthesis so the two can be
                # compared without hunting for the source file
                ref, _ = librosa.load(s["wav"], sr=44100)
                sf.write(f"{d}/ref.wav", ref, 44100)
                for ti, pr2 in enumerate(prompts, 1):
                    w = tts_forward(pr2["ids"], pr2["msk"], s["best_ttl"], s["dp"],
                                    dp, te, ve, voc, TOTAL_STEP, SPEED,
                                    pr2["lat"], pr2["lmask"])[0]
                    sf.write(f"{d}/t{ti}.wav",
                             w.detach().reshape(-1).cpu().numpy(), 44100)
            for k in ("ttl", "best_ttl", "opt", "sched", "tgt", "dp"):
                s.pop(k, None)
        if finished:
            admit()

    with open(f"{args.out}/summary.json", "w", encoding="utf-8") as f:
        json.dump({"layer": args.layer, "threshold": args.threshold,
                   "batch": args.batch, "speakers": done}, f, indent=2)

    conv = sum(d["converged"] for d in done)
    steps = [d["steps"] for d in done]
    el = (time.time() - t_start) / 60
    print(f"\n{conv}/{len(done)} converged below {args.threshold}")
    print(f"steps: mean {np.mean(steps):.0f}  range {min(steps)}-{max(steps)}")
    print(f"wall clock: {el:.1f} min ({el / max(1, len(done)):.2f} min/speaker)")
    print(f"summary -> {args.out}/summary.json")


if __name__ == "__main__":
    main()
