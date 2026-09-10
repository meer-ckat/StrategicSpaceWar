"""SIM_W (WavLM-base-plus-sv) for an extraction run, extraction and baseline.

This is the speaker-similarity metric most TTS work reports, so it is the number
comparable to published systems. It is NOT an independent check of our method:
it shares the WavLM family with the optimization objective. ECAPA and ResNet in
measure_all.py are the independent measures.

Kept in its own file because speechbrain and transformers pull in conflicting
dependencies when loaded in the same process.

Usage:  python src/eval_simw.py results/main_layer4
"""
import csv
import json
import os
import sys

import numpy as np
import torch
import torchaudio
from transformers import AutoFeatureExtractor, WavLMForXVector

MODEL = "microsoft/wavlm-base-plus-sv"
N_TEXTS = 5

fe = AutoFeatureExtractor.from_pretrained(MODEL)
model = WavLMForXVector.from_pretrained(MODEL).cuda().eval()


def emb(path):
    w, sr = torchaudio.load(path)
    if w.shape[0] > 1:
        w = w.mean(0, keepdim=True)
    if sr != 16000:
        w = torchaudio.functional.resample(w, sr, 16000)
    x = fe(w.squeeze().numpy(), sampling_rate=16000,
           return_tensors="pt", padding=True)
    with torch.no_grad():
        return model(x.input_values.cuda()).embeddings.squeeze()


def cos(a, b):
    return float(torch.nn.functional.cosine_similarity(a, b, dim=0))


run = sys.argv[1] if len(sys.argv) > 1 else "results/main_layer4"
summary = json.load(open(f"{run}/summary.json", encoding="utf-8"))
spk = summary["speakers"]
print(f"{run}: {len(spk)} speakers\n")

for s in spk:
    base = f"{run}/wav/{s['id']}"
    ref = emb(f"{base}/ref.wav")
    for cond, sub in (("opt", ""), ("pre", "/preset")):
        sims = []
        for t in range(1, N_TEXTS + 1):
            p = f"{base}{sub}/t{t}.wav"
            if os.path.exists(p):
                sims.append(cos(ref, emb(p)))
        s[f"{cond}_wavlmsv"] = float(np.mean(sims)) if sims else float("nan")


def block(rows, label):
    if not rows:
        return
    pre = np.array([r["pre_wavlmsv"] for r in rows])
    opt = np.array([r["opt_wavlmsv"] for r in rows])
    print(f"  {label:<10} n={len(rows):3d}   preset {pre.mean():.4f}   "
          f"proposed {opt.mean():.4f}   delta {opt.mean()-pre.mean():+.4f}   "
          f"improved {int((opt > pre).sum())}/{len(rows)}")


print("SIM_W (wavlm-base-plus-sv)")
block(spk, "ALL")
for c in ("vctk", "seedtts"):
    block([r for r in spk if r.get("corpus") == c], c)

opt = np.array([s["opt_wavlmsv"] for s in spk])
print(f"\n  range {opt.min():.4f} - {opt.max():.4f}   median {np.median(opt):.4f}")

# merge into the existing metrics table
mp = f"{run}/metrics.csv"
if os.path.exists(mp):
    rows = list(csv.DictReader(open(mp, newline="", encoding="utf-8")))
    by = {s["id"]: s for s in spk}
    for r in rows:
        if r["id"] in by:
            r["opt_wavlmsv"] = by[r["id"]]["opt_wavlmsv"]
            r["pre_wavlmsv"] = by[r["id"]]["pre_wavlmsv"]
    cols = list(rows[0].keys())
    with open(mp, "w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=cols, extrasaction="ignore")
        w.writeheader()
        w.writerows(rows)
    print(f"\nmerged into {mp}")

json.dump(summary, open(f"{run}/summary.json", "w", encoding="utf-8"), indent=2)
print(f"\nNote: SIM_W shares the WavLM family with the optimization objective, "
      f"so it is the comparable-to-literature number, not an independent one.")
