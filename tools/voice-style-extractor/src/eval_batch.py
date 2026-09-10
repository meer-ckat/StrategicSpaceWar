"""Did threshold 0.30 at layer 4 keep the audio usable?

Speaker similarity alone cannot answer this: a cosine between speaker embeddings
can stay high on degraded audio. So this measures ECAPA similarity AND word error
rate, and puts both next to the published layer-3 numbers for the same speakers.
"""
import csv
import glob
import json
import os
import sys

import numpy as np
import torch
import torchaudio

from core import TEXTS, wer

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # repo root, one level above src/
PILOT = sys.argv[1] if len(sys.argv) > 1 else f"{ROOT}/results/pilot_layer4/thr030"

summary = json.load(open(f"{PILOT}/summary.json", encoding="utf-8"))
spk = summary["speakers"]
print(f"layer {summary['layer']}  threshold {summary['threshold']}  "
      f"{len(spk)} speakers\n")

# published layer-3 values for the same speakers
pub = {}
with open(f"{ROOT}/results/_layer3_seedtts/sim_all_speakers.csv", newline="", encoding="utf-8") as f:
    for r in csv.DictReader(f):
        pub[r["id"]] = float(r["opt_ecapa"])

# ---- ECAPA similarity ------------------------------------------------------
from speechbrain.inference.speaker import EncoderClassifier
enc = EncoderClassifier.from_hparams(
    source="speechbrain/spkrec-ecapa-voxceleb",
    savedir=os.path.join(os.environ.get("TEMP", "/tmp"), "sb_ecapa"),
    run_opts={"device": "cuda"})


def emb(path):
    w, sr = torchaudio.load(path)
    if w.shape[0] > 1:
        w = w.mean(0, keepdim=True)
    if sr != 16000:
        w = torchaudio.functional.resample(w, sr, 16000)
    with torch.no_grad():
        return enc.encode_batch(w.cuda()).squeeze()


rows = []
for s in spk:
    ref = emb(s["wav"])
    sims = []
    for t in range(1, 6):
        p = f"{PILOT}/wav/{s['id']}/t{t}.wav"
        if os.path.exists(p):
            sims.append(float(torch.nn.functional.cosine_similarity(
                ref, emb(p), dim=0)))
    rows.append({"id": s["id"], "loss": s["best_loss"], "steps": s["steps"],
                 "sim": float(np.mean(sims)) if sims else float("nan")})

# ---- WER -------------------------------------------------------------------
from faster_whisper import WhisperModel
wm = WhisperModel("large-v3", device="cuda", compute_type="float16")
for r in rows:
    ws = []
    for t in range(1, 6):
        p = f"{PILOT}/wav/{r['id']}/t{t}.wav"
        if not os.path.exists(p):
            continue
        segs, _ = wm.transcribe(p, language="en", beam_size=5)
        ws.append(wer(TEXTS[t - 1], " ".join(x.text for x in segs).strip()))
    r["wer"] = float(np.mean(ws)) if ws else float("nan")

print(f"{'id':>4} {'loss':>7} {'steps':>6} {'SIM_E(L4)':>10} "
      f"{'SIM_E(L3 pub)':>14} {'delta':>7} {'WER':>7}")
print("-" * 62)
for r in rows:
    old = pub.get(r["id"])
    d = r["sim"] - old if old is not None else float("nan")
    print(f"{r['id']:>4} {r['loss']:>7.4f} {r['steps']:>6} {r['sim']:>10.4f} "
          f"{(old if old is not None else float('nan')):>14.4f} "
          f"{d:>+7.4f} {r['wer']*100:>6.2f}%")

sims = [r["sim"] for r in rows]
wers = [r["wer"] for r in rows]
olds = [pub[r["id"]] for r in rows if r["id"] in pub]
print("-" * 62)
print(f"{'mean':>4} {'':>7} {'':>6} {np.mean(sims):>10.4f} "
      f"{np.mean(olds):>14.4f} {np.mean(sims)-np.mean(olds):>+7.4f} "
      f"{np.mean(wers)*100:>6.2f}%")
print(f"\npublished 44-speaker layer-3 reference: SIM_E 0.4515, WER 2.70%")
print("A WER near or below that means threshold 0.30 did not damage the audio.")
