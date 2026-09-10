"""What does a perfect match actually score, and what does an unrelated voice?

Speaker-similarity numbers mean nothing without both ends of the scale. This
measures them on the SAME references the extraction run used:

  ceiling - two halves of one recording scored against each other. Speaker and
            channel are held fixed and only content varies, which is exactly the
            nuisance our objective time-averages away.
  floor   - different speakers' recordings scored against each other.

Anything above the ceiling would mean the metric is tracking something other
than speaker identity.

Usage:  python src/speaker_ceiling.py results/main_layer4
"""
import argparse
import glob
import itertools
import json
import os

import numpy as np
import torch
import torchaudio


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run", nargs="?", default="results/main_layer4")
    ap.add_argument("--min-dur", type=float, default=6.0,
                    help="each half must still be long enough to score")
    ap.add_argument("--max-pairs", type=int, default=4000,
                    help="cap the impostor pairs; they grow quadratically")
    args = ap.parse_args()

    from speechbrain.inference.speaker import EncoderClassifier
    enc = EncoderClassifier.from_hparams(
        source="speechbrain/spkrec-ecapa-voxceleb",
        savedir=os.path.join(os.environ.get("TEMP", "/tmp"), "sb_ecapa"),
        run_opts={"device": "cuda"})

    def embed(w):
        with torch.no_grad():
            return enc.encode_batch(w.cuda()).squeeze()

    def cos(a, b):
        return float(torch.nn.functional.cosine_similarity(a, b, dim=0))

    refs = sorted(glob.glob(f"{args.run}/wav/*/ref.wav"))
    print(f"references found: {len(refs)}")

    halves, whole, kept = {}, {}, []
    for p in refs:
        w, sr = torchaudio.load(p)
        if w.shape[0] > 1:
            w = w.mean(0, keepdim=True)
        if sr != 16000:
            w = torchaudio.functional.resample(w, sr, 16000)
        if w.shape[-1] / 16000 < args.min_dur:
            continue
        sid = os.path.basename(os.path.dirname(p))
        h = w.shape[-1] // 2
        halves[sid] = (embed(w[:, :h]), embed(w[:, h:]))
        whole[sid] = embed(w)
        kept.append(sid)

    same = np.array([cos(*halves[s]) for s in kept])

    rng = np.random.default_rng(0)
    pairs = list(itertools.combinations(kept, 2))
    if len(pairs) > args.max_pairs:
        idx = rng.choice(len(pairs), args.max_pairs, replace=False)
        pairs = [pairs[i] for i in idx]
    diff = np.array([cos(whole[a], whole[b]) for a, b in pairs])

    print(f"used {len(kept)} references (>= {args.min_dur}s), "
          f"{len(diff)} impostor pairs\n")
    print("SAME speaker (two halves of one recording) - the ceiling")
    print(f"  mean {same.mean():.4f}  median {np.median(same):.4f}  "
          f"sd {same.std(ddof=1):.4f}  range {same.min():.4f}-{same.max():.4f}")
    print("\nDIFFERENT speakers - the floor")
    print(f"  mean {diff.mean():.4f}  median {np.median(diff):.4f}  "
          f"95th pct {np.percentile(diff, 95):.4f}  max {diff.max():.4f}")

    lo, hi = diff.mean(), same.mean()
    print(f"\nSIM_E runs from about {lo:.2f} (unrelated) to {hi:.2f} (same voice).")
    metrics = f"{args.run}/metrics.csv"
    if os.path.exists(metrics):
        import csv
        rows = list(csv.DictReader(open(metrics, newline="", encoding="utf-8")))
        for col, name in (("pre_ecapa", "nearest preset"), ("opt_ecapa", "proposed")):
            v = float(np.mean([float(r[col]) for r in rows]))
            print(f"  {name:<16} {v:.4f}  =  {100*(v-lo)/(hi-lo):5.1f}% of the way "
                  f"from floor to ceiling  ({100*v/hi:.0f}% of the ceiling)")

    out = f"{args.run}/speaker_ceiling.json"
    json.dump({"n_refs": len(kept), "min_dur": args.min_dur,
               "same_mean": float(same.mean()), "same_sd": float(same.std(ddof=1)),
               "same_min": float(same.min()), "same_max": float(same.max()),
               "diff_mean": float(diff.mean()), "diff_p95": float(np.percentile(diff, 95)),
               "diff_max": float(diff.max()), "n_pairs": len(diff)},
              open(out, "w", encoding="utf-8"), indent=2)
    print(f"\nwrote {out}")


if __name__ == "__main__":
    main()
