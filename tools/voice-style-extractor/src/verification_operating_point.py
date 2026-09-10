"""At what false-accept rate would a verifier accept our extracted voices?

A raw cosine similarity is not interpretable on its own. What matters for the
threat model is where the extracted styles land against a verifier's decision
threshold: if a system is tuned to accept genuine speakers at some false-accept
rate, how many of our extractions does it accept?

Builds three distributions on the same references the extraction used:
  genuine  - two halves of one recording (an upper bound on any extraction)
  impostor - different speakers
  attack   - extracted style vs the speaker it was extracted from
then reports the operating points.

Usage:  python src/verification_operating_point.py results/main_layer4
"""
import argparse
import csv
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
    ap.add_argument("--min-dur", type=float, default=6.0)
    ap.add_argument("--max-pairs", type=int, default=6000)
    args = ap.parse_args()

    from speechbrain.inference.speaker import EncoderClassifier
    enc = EncoderClassifier.from_hparams(
        source="speechbrain/spkrec-ecapa-voxceleb",
        savedir=os.path.join(os.environ.get("TEMP", "/tmp"), "sb_ecapa"),
        run_opts={"device": "cuda"})

    def embed(w):
        with torch.no_grad():
            return enc.encode_batch(w.cuda()).squeeze()

    def load(p):
        w, sr = torchaudio.load(p)
        if w.shape[0] > 1:
            w = w.mean(0, keepdim=True)
        if sr != 16000:
            w = torchaudio.functional.resample(w, sr, 16000)
        return w

    def cos(a, b):
        return float(torch.nn.functional.cosine_similarity(a, b, dim=0))

    whole = {}
    for p in sorted(glob.glob(f"{args.run}/wav/*/ref.wav")):
        whole[os.path.basename(os.path.dirname(p))] = embed(load(p))

    # Genuine pairs are two DIFFERENT utterances of one speaker, not two halves
    # of one recording: halves share channel and session exactly, which inflates
    # the ceiling. A deployed verifier enrols one recording and is presented with
    # a different one, which is what this measures.
    import csv as _csv
    import random
    rng_g = random.Random(0)
    meta = {c["spk"]: c for c in json.load(open("data/vctk_speakers.json",
                                                encoding="utf-8"))}
    genuine = []
    for sid, m in meta.items():
        if sid not in whole:
            continue
        used = os.path.basename(m["path"])
        cands = [q for q in glob.glob(f"data/vctk/wav48_silence_trimmed/{sid}/*mic1*.flac")
                 if os.path.basename(q) != used]
        if not cands:
            continue
        try:
            genuine.append(cos(whole[sid], embed(load(rng_g.choice(cands)))))
        except Exception:
            pass
    genuine = np.array(genuine)

    rng = np.random.default_rng(0)
    ids = sorted(whole)
    pairs = list(itertools.combinations(ids, 2))
    if len(pairs) > args.max_pairs:
        pairs = [pairs[i] for i in rng.choice(len(pairs), args.max_pairs, replace=False)]
    impostor = np.array([cos(whole[a], whole[b]) for a, b in pairs])

    rows = list(csv.DictReader(open(f"{args.run}/metrics.csv", newline="",
                                    encoding="utf-8")))
    attack = np.array([float(r["opt_ecapa"]) for r in rows])
    baseline = np.array([float(r["pre_ecapa"]) for r in rows])

    def far(t):
        return float((impostor >= t).mean())

    def thr_at_far(target):
        return float(np.quantile(impostor, 1 - target))

    print(f"genuine  n={len(genuine):4d}  mean {genuine.mean():.3f}  "
          f"median {np.median(genuine):.3f}")
    print(f"impostor n={len(impostor):4d}  mean {impostor.mean():.3f}  "
          f"median {np.median(impostor):.3f}")
    print(f"attack   n={len(attack):4d}  mean {attack.mean():.3f}  "
          f"median {np.median(attack):.3f}\n")

    # equal error rate on genuine vs impostor
    grid = np.linspace(-0.2, 1.0, 2401)
    frr = np.array([(genuine < t).mean() for t in grid])
    fa = np.array([(impostor >= t).mean() for t in grid])
    i = int(np.argmin(np.abs(frr - fa)))
    eer_t, eer = float(grid[i]), float((frr[i] + fa[i]) / 2)
    print(f"EER on this data: {100*eer:.1f}% at threshold {eer_t:.3f}")
    print(f"  genuine accepted at that threshold : "
          f"{100*(genuine >= eer_t).mean():.1f}%")
    print(f"  ATTACK  accepted at that threshold : "
          f"{100*(attack >= eer_t).mean():.1f}%")
    print(f"  baseline accepted at that threshold: "
          f"{100*(baseline >= eer_t).mean():.1f}%\n")

    print(f"{'operating point':<22}{'threshold':>11}{'genuine':>10}"
          f"{'ATTACK':>10}{'preset':>9}")
    for target in (0.10, 0.05, 0.01, 0.001):
        t = thr_at_far(target)
        print(f"  FAR = {100*target:>5.1f}%{'':<9}{t:>11.3f}"
              f"{100*(genuine >= t).mean():>9.1f}%"
              f"{100*(attack >= t).mean():>9.1f}%"
              f"{100*(baseline >= t).mean():>8.1f}%")

    print(f"\nour mean attack score {attack.mean():.3f} corresponds to "
          f"FAR = {100*far(attack.mean()):.2f}%")
    print("i.e. a verifier loose enough to accept it would also accept that "
          "fraction of unrelated voices.")

    out = f"{args.run}/verification_operating_point.json"
    json.dump({"eer": eer, "eer_threshold": eer_t,
               "genuine_median": float(np.median(genuine)),
               "impostor_median": float(np.median(impostor)),
               "attack_mean": float(attack.mean()),
               "attack_accept_at_eer": float((attack >= eer_t).mean()),
               "baseline_accept_at_eer": float((baseline >= eer_t).mean()),
               "far_at_attack_mean": far(attack.mean()),
               "n_genuine": len(genuine), "n_impostor": len(impostor)},
              open(out, "w", encoding="utf-8"), indent=2)
    print(f"\nwrote {out}")


if __name__ == "__main__":
    main()
