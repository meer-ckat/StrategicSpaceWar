"""Speaker similarity (ECAPA, ResNet), WER, and preset-selection accuracy.

Run after run_batch_extract.py. SIM_W lives in eval_simw.py because
transformers and speechbrain pull in conflicting dependencies when loaded in
the same process.

For the nearest-preset baseline this synthesizes the same five prompts from the
preset each speaker started from, so the comparison is like for like.

Usage:  python src/measure_all.py results/main_layer4
"""
import argparse
import csv
import json
import os

import numpy as np
import soundfile as sf
import torch
import torchaudio

from core import (SPEED, TOTAL_STEP, build_prompts, load_pipeline,
                  preset_tensors, tts_forward, wer, TEXTS)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run")
    ap.add_argument("--skip-preset", action="store_true",
                    help="skip synthesizing the baseline (slow) and score only the extraction")
    args = ap.parse_args()

    summary = json.load(open(f"{args.run}/summary.json", encoding="utf-8"))
    spk = summary["speakers"]
    print(f"{args.run}: layer {summary['layer']} threshold {summary['threshold']} "
          f"{len(spk)} speakers\n")

    # ---- baseline audio: the preset each speaker was initialized from -----
    if not args.skip_preset:
        models = load_pipeline()
        tts, dp = models[0], models[1]
        prompts = build_prompts(tts, dp)
        for s in spk:
            d = f"{args.run}/wav/{s['id']}/preset"
            if os.path.isdir(d) and len(os.listdir(d)) >= len(prompts):
                continue
            os.makedirs(d, exist_ok=True)
            ttl, sdp = preset_tensors(f"models/voice_styles/{s['preset']}.json")
            for ti, pr in enumerate(prompts, 1):
                w = tts_forward(pr["ids"], pr["msk"], ttl, sdp, *models[1:],
                                TOTAL_STEP, SPEED, pr["lat"], pr["lmask"])[0]
                sf.write(f"{d}/t{ti}.wav", w.detach().reshape(-1).cpu().numpy(), 44100)
        print("preset baseline synthesized\n")
        del models
        torch.cuda.empty_cache()

    # ---- speaker similarity ---------------------------------------------
    from speechbrain.inference.speaker import EncoderClassifier
    tmpdir = os.environ.get("TEMP", "/tmp")
    encoders = {
        "ecapa": EncoderClassifier.from_hparams(
            source="speechbrain/spkrec-ecapa-voxceleb",
            savedir=os.path.join(tmpdir, "sb_ecapa"), run_opts={"device": "cuda"}),
        "resnet": EncoderClassifier.from_hparams(
            source="speechbrain/spkrec-resnet-voxceleb",
            savedir=os.path.join(tmpdir, "sb_resnet"), run_opts={"device": "cuda"}),
    }

    def emb(enc, path):
        w, sr = torchaudio.load(path)
        if w.shape[0] > 1:
            w = w.mean(0, keepdim=True)
        if sr != 16000:
            w = torchaudio.functional.resample(w, sr, 16000)
        with torch.no_grad():
            return enc.encode_batch(w.cuda()).squeeze()

    for s in spk:
        base = f"{args.run}/wav/{s['id']}"
        for tag, enc in encoders.items():
            ref = emb(enc, f"{base}/ref.wav")
            for cond, sub in (("opt", ""), ("pre", "/preset")):
                sims = []
                for t in range(1, len(TEXTS) + 1):
                    p = f"{base}{sub}/t{t}.wav"
                    if os.path.exists(p):
                        sims.append(float(torch.nn.functional.cosine_similarity(
                            ref, emb(enc, p), dim=0)))
                s[f"{cond}_{tag}"] = float(np.mean(sims)) if sims else float("nan")
    del encoders
    torch.cuda.empty_cache()
    print("similarity done")

    # ---- WER -------------------------------------------------------------
    from faster_whisper import WhisperModel
    wm = WhisperModel("large-v3", device="cuda", compute_type="float16")
    for s in spk:
        base = f"{args.run}/wav/{s['id']}"
        for cond, sub in (("opt", ""), ("pre", "/preset")):
            ws = []
            for t, text in enumerate(TEXTS, 1):
                p = f"{base}{sub}/t{t}.wav"
                if not os.path.exists(p):
                    continue
                segs, _ = wm.transcribe(p, language="en", beam_size=5)
                ws.append(wer(text, " ".join(x.text for x in segs).strip()))
            s[f"{cond}_wer"] = float(np.mean(ws)) if ws else float("nan")
    print("wer done\n")

    # ---- report ----------------------------------------------------------
    def block(rows, label):
        if not rows:
            return
        print(f"--- {label}  (n={len(rows)}) ---")
        print(f"{'metric':<10}{'preset':>10}{'proposed':>11}{'delta':>10}"
              f"{'improved':>11}")
        for m in ("ecapa", "resnet", "wer"):
            pre = np.array([r[f"pre_{m}"] for r in rows])
            opt = np.array([r[f"opt_{m}"] for r in rows])
            better = (opt < pre) if m == "wer" else (opt > pre)
            scale = 100 if m == "wer" else 1
            unit = "%" if m == "wer" else " "
            print(f"{m.upper():<10}{pre.mean()*scale:>9.3f}{unit}"
                  f"{opt.mean()*scale:>10.3f}{unit}"
                  f"{(opt.mean()-pre.mean())*scale:>+9.3f}{unit}"
                  f"{int(better.sum()):>7}/{len(rows)}")
        print()

    block(spk, "ALL")
    for c in ("vctk", "seedtts"):
        block([r for r in spk if r.get("corpus") == c], c)

    # ---- preset gender accuracy against VCTK's real labels ---------------
    v = [r for r in spk if r.get("corpus") == "vctk" and r.get("gender")]
    if v:
        ok = sum(1 for r in v if r["preset"][0].upper() == r["gender"].upper())
        print(f"preset gender vs VCTK metadata: {ok}/{len(v)} = {100*ok/len(v):.1f}%")
        bad = [(r["id"], r["preset"], r["gender"]) for r in v
               if r["preset"][0].upper() != r["gender"].upper()]
        if bad:
            print("  mismatches:", ", ".join(f"{i}({p} vs {g})" for i, p, g in bad[:12]))

    with open(f"{args.run}/metrics.csv", "w", newline="", encoding="utf-8") as f:
        cols = ["id", "corpus", "preset", "gender", "accent", "best_loss", "steps",
                "opt_ecapa", "pre_ecapa", "opt_resnet", "pre_resnet",
                "opt_wer", "pre_wer"]
        w = csv.DictWriter(f, fieldnames=cols, extrasaction="ignore")
        w.writeheader()
        w.writerows(spk)
    json.dump(summary, open(f"{args.run}/summary.json", "w", encoding="utf-8"), indent=2)
    print(f"\nwrote {args.run}/metrics.csv")


if __name__ == "__main__":
    main()
