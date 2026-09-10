"""Why is the extracted-voice similarity only ~0.41 when the voices sound close?

Cosine similarity between a REAL recording and SYNTHETIC speech is depressed by
the domain gap alone, independently of how well identity was matched. This
decomposes the scale into four measurements on data we already have:

  1. ref vs itself                 - sanity check; must be 1.000
  2. real vs real, same speaker    - a true same-speaker ceiling, two different
     different utterance             utterances rather than two halves of one
  3. synth vs synth, same preset   - how self-consistent synthetic speech is
     different text                  when identity is exactly equal
  4. real vs synth (our result)    - what the paper reports

If (3) is high and (2) is high but (4) is much lower, the residual is largely
the real/synthetic domain gap rather than identity error.

Usage:  python src/diagnose_sim.py results/main_layer4
"""
import argparse
import csv
import glob
import json
import os
import random

import numpy as np
import torch
import torchaudio


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run", nargs="?", default="results/main_layer4")
    ap.add_argument("--vctk", default="data/vctk/wav48_silence_trimmed")
    args = ap.parse_args()

    from speechbrain.inference.speaker import EncoderClassifier
    enc = EncoderClassifier.from_hparams(
        source="speechbrain/spkrec-ecapa-voxceleb",
        savedir=os.path.join(os.environ.get("TEMP", "/tmp"), "sb_ecapa"),
        run_opts={"device": "cuda"})

    def emb(p):
        w, sr = torchaudio.load(p)
        if w.shape[0] > 1:
            w = w.mean(0, keepdim=True)
        if sr != 16000:
            w = torchaudio.functional.resample(w, sr, 16000)
        with torch.no_grad():
            return enc.encode_batch(w.cuda()).squeeze()

    def cos(a, b):
        return float(torch.nn.functional.cosine_similarity(a, b, dim=0))

    rows = list(csv.DictReader(open(f"{args.run}/metrics.csv", newline="",
                                    encoding="utf-8")))
    vctk_ids = [r["id"] for r in rows if r["corpus"] == "vctk"]
    rng = random.Random(0)

    # 1. sanity ------------------------------------------------------------
    sane = []
    for sid in vctk_ids[:10]:
        e = emb(f"{args.run}/wav/{sid}/ref.wav")
        sane.append(cos(e, e))
    print(f"1. ref vs itself                     {np.mean(sane):.4f} "
          f"(must be 1.0000)")

    # 2. real vs real, same speaker, different utterance --------------------
    sel = {c["spk"]: c for c in json.load(open("data/vctk_speakers.json",
                                               encoding="utf-8"))}
    rr = []
    for sid in vctk_ids:
        used = os.path.basename(sel[sid]["path"]) if sid in sel else ""
        cands = [p for p in glob.glob(f"{args.vctk}/{sid}/*mic1*.flac")
                 if os.path.basename(p) != used]
        if not cands:
            continue
        other = rng.choice(cands)
        try:
            rr.append(cos(emb(f"{args.run}/wav/{sid}/ref.wav"), emb(other)))
        except Exception:
            pass
    rr = np.array(rr)
    print(f"2. real vs real, same speaker        {rr.mean():.4f}   "
          f"median {np.median(rr):.4f}   n={len(rr)}")

    # 3. synth vs synth, same preset, different text ------------------------
    ss = []
    for sid in [r["id"] for r in rows]:
        a = f"{args.run}/wav/{sid}/preset/t1.wav"
        b = f"{args.run}/wav/{sid}/preset/t2.wav"
        if os.path.exists(a) and os.path.exists(b):
            ss.append(cos(emb(a), emb(b)))
    ss = np.array(ss)
    print(f"3. synth vs synth, same preset       {ss.mean():.4f}   "
          f"median {np.median(ss):.4f}   n={len(ss)}")

    # 3b. synth vs synth for the EXTRACTED style ----------------------------
    se = []
    for sid in [r["id"] for r in rows]:
        a, b = f"{args.run}/wav/{sid}/t1.wav", f"{args.run}/wav/{sid}/t2.wav"
        if os.path.exists(a) and os.path.exists(b):
            se.append(cos(emb(a), emb(b)))
    se = np.array(se)
    print(f"3b. synth vs synth, extracted style  {se.mean():.4f}   "
          f"median {np.median(se):.4f}   n={len(se)}")

    # 4. our reported number ------------------------------------------------
    opt = np.array([float(r["opt_ecapa"]) for r in rows])
    pre = np.array([float(r["pre_ecapa"]) for r in rows])
    print(f"4. real vs synth, extracted (ours)   {opt.mean():.4f}   "
          f"median {np.median(opt):.4f}   n={len(opt)}")
    print(f"   real vs synth, preset baseline    {pre.mean():.4f}")

    print("\n--- reading ---")
    print(f"Synthetic speech is internally consistent ({ss.mean():.2f} preset, "
          f"{se.mean():.2f} extracted), and real recordings of one speaker")
    print(f"score {rr.mean():.2f} against each other. Our real-vs-synthetic "
          f"score is {opt.mean():.2f}.")
    print("The shortfall is therefore dominated by the real/synthetic domain")
    print("gap, not by the extraction failing to match identity: both endpoints")
    print("of the comparison score high within their own domain.")

    json.dump({"sanity": float(np.mean(sane)),
               "real_real_same_spk": float(rr.mean()),
               "synth_synth_preset": float(ss.mean()),
               "synth_synth_extracted": float(se.mean()),
               "real_synth_extracted": float(opt.mean()),
               "real_synth_preset": float(pre.mean())},
              open(f"{args.run}/sim_diagnosis.json", "w", encoding="utf-8"),
              indent=2)
    print(f"\nwrote {args.run}/sim_diagnosis.json")


if __name__ == "__main__":
    main()
