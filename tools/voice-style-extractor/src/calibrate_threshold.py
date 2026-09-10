"""Recalibrate the early-stopping threshold for a given WavLM layer.

The stopping rule compares the optimization loss against the floor that two
DIFFERENT sentences share when synthesized from the SAME preset. That floor is
content mismatch, not speaker mismatch, so it marks the point past which further
optimization pushes the style vector off the manifold rather than toward the
target. The floor's numeric scale depends on the layer, so switching layers
requires redoing this measurement.

Usage:  python src/calibrate_threshold.py --layers 3,4 --texts 4
"""
import argparse
import itertools
import json
import os

import numpy as np

from core import (build_prompts, load_pipeline, load_wavlm, preset_paths,
                  preset_tensors, style_loss, synthesize, to16k, wav_stats)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--layers", default="4")
    ap.add_argument("--texts", type=int, default=4, help="prompts per preset")
    ap.add_argument("--threshold", type=float, default=0.30,
                    help="the value being justified; reported as a percentile")
    ap.add_argument("--out", default="results/threshold_calibration.json")
    args = ap.parse_args()
    layers = [int(x) for x in args.layers.split(",")]

    models = load_pipeline()
    tts, dp = models[0], models[1]
    wavlm = load_wavlm()
    prompts = build_prompts(tts, dp)[:args.texts]
    presets = preset_paths()
    names = [os.path.basename(p).replace(".json", "") for p in presets]
    print(f"presets {len(presets)}   prompts {len(prompts)}   layers {layers}\n")

    # every (preset, prompt) pair synthesized once, reused across layers
    wavs = {}
    for p, name in zip(presets, names):
        ttl, sdp = preset_tensors(p)
        for ti, pr in enumerate(prompts):
            wavs[(name, ti)] = synthesize(pr, ttl, sdp, models)
        print(f"  synthesized {name}")
    print()

    out = {}
    for layer in layers:
        feats = {k: wav_stats(wavlm, to16k(w.unsqueeze(0)), layer)
                 for k, w in wavs.items()}

        per_preset = {}
        for n in names:
            v = [style_loss(feats[(n, i)], feats[(n, j)]).item()
                 for i, j in itertools.combinations(range(len(prompts)), 2)]
            per_preset[n] = {"min": round(float(min(v)), 4),
                             "max": round(float(max(v)), 4),
                             "mean": round(float(np.mean(v)), 4),
                             "pairs": [round(float(x), 4) for x in v]}

        same = np.array([x for d in per_preset.values() for x in d["pairs"]])
        cross = np.array([style_loss(feats[(a, i)], feats[(b, i)]).item()
                          for a, b in itertools.combinations(names, 2)
                          for i in range(len(prompts))])

        print(f"=== layer {layer}: per-preset floor ===")
        print(f"  {'preset':<8}{'min':>9}{'mean':>9}{'max':>9}   pairs")
        for n, d in per_preset.items():
            print(f"  {n:<8}{d['min']:>9.4f}{d['mean']:>9.4f}{d['max']:>9.4f}   "
                  + " ".join(f"{x:.3f}" for x in d["pairs"]))
        print(f"\n  same preset, different text : {same.min():.4f} - {same.max():.4f}"
              f"  (mean {same.mean():.4f}, n={len(same)})")
        print(f"  different presets           : {cross.min():.4f} - {cross.max():.4f}"
              f"  (mean {cross.mean():.4f}, n={len(cross)})")
        pct = float((same < args.threshold).mean() * 100)
        print(f"  a threshold of {args.threshold} sits below {pct:.0f}% of "
              f"same-voice pairs")
        if cross.min() <= same.max():
            print("  note: the two distributions overlap, so this floor is a "
                  "content residual, not a speaker decision boundary")
        print()

        out[layer] = {
            "same_min": float(same.min()), "same_max": float(same.max()),
            "same_mean": float(same.mean()),
            "cross_min": float(cross.min()), "cross_mean": float(cross.mean()),
            "threshold": args.threshold, "pct_below_threshold": pct,
            "per_preset": per_preset,
        }

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as f:
        json.dump(out, f, indent=2)
    print(f"wrote {args.out}")


if __name__ == "__main__":
    main()
