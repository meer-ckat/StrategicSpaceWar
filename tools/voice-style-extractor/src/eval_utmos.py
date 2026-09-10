"""Predicted naturalness (UTMOS) for the extracted styles and their preset baseline.

UTMOS is a MOS *predictor*, not a listening test, and it was trained on VoiceMOS
Challenge 2022 systems, so absolute values on a 2026 model are out of domain. The
paired preset-vs-extracted comparison is the usable quantity: same synthesizer,
same vocoder, same five prompts, so systematic bias largely cancels.

Writes results/<run>/utmos.json and adds opt_utmos / pre_utmos / ref_utmos to
metrics.csv.

    python src/eval_utmos.py --run results/main_layer4
"""
import argparse, csv, json, os, statistics as st

import librosa
import torch

HUB = ("tarepan/SpeechMOS:v1.2.0", "utmos22_strong")


def load_model(device):
    m = torch.hub.load(*HUB, trust_repo=True)
    return m.eval().to(device)


def score(model, path, device):
    x, _ = librosa.load(path, sr=16000)
    with torch.no_grad():
        t = torch.from_numpy(x).unsqueeze(0).to(device)
        return float(model(t, 16000).item())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--run", default="results/main_layer4")
    ap.add_argument("--device", default="cuda" if torch.cuda.is_available() else "cpu")
    args = ap.parse_args()

    wav_root = os.path.join(args.run, "wav")
    csv_path = os.path.join(args.run, "metrics.csv")
    rows = list(csv.DictReader(open(csv_path)))
    model = load_model(args.device)

    per_speaker = {}
    for n, r in enumerate(rows, 1):
        spk = r["id"]
        d = os.path.join(wav_root, spk)
        opt = [score(model, os.path.join(d, f"t{t}.wav"), args.device)
               for t in range(1, 6) if os.path.exists(os.path.join(d, f"t{t}.wav"))]
        pre = [score(model, os.path.join(d, "preset", f"t{t}.wav"), args.device)
               for t in range(1, 6) if os.path.exists(os.path.join(d, "preset", f"t{t}.wav"))]
        ref_p = os.path.join(d, "ref.wav")
        ref = score(model, ref_p, args.device) if os.path.exists(ref_p) else None
        per_speaker[spk] = {"opt": opt, "pre": pre, "ref": ref,
                            "opt_mean": st.mean(opt) if opt else None,
                            "pre_mean": st.mean(pre) if pre else None,
                            "corpus": r["corpus"]}
        if n % 20 == 0 or n == len(rows):
            print(f"  {n}/{len(rows)}", flush=True)

    def agg(key, pred=None):
        v = [d[key] for d in per_speaker.values()
             if d[key] is not None and (pred is None or pred(d))]
        return st.mean(v), len(v)

    print()
    for name, pred in [("ALL", None),
                       ("VCTK", lambda d: d["corpus"] == "vctk"),
                       ("SEED", lambda d: d["corpus"] != "vctk")]:
        pm, n = agg("pre_mean", pred)
        om, _ = agg("opt_mean", pred)
        rv, _ = agg("ref", pred)
        print(f"{name:5s} n={n:3d}  preset {pm:.3f} -> extracted {om:.3f} "
              f"(delta {om - pm:+.3f})   real reference {rv:.3f}")

    worse = sum(1 for d in per_speaker.values()
                if d["opt_mean"] is not None and d["opt_mean"] < d["pre_mean"])
    print(f"\nspeakers where extraction scores lower than its preset: {worse}/{len(per_speaker)}")

    out = os.path.join(args.run, "utmos.json")
    json.dump({"model": "utmos22_strong (tarepan/SpeechMOS v1.2.0)",
               "per_speaker": per_speaker}, open(out, "w"), indent=1)
    print("wrote", out)

    fields = list(rows[0].keys())
    for c in ("opt_utmos", "pre_utmos", "ref_utmos"):
        if c not in fields:
            fields.append(c)
    for r in rows:
        d = per_speaker[r["id"]]
        r["opt_utmos"] = f"{d['opt_mean']:.4f}" if d["opt_mean"] is not None else ""
        r["pre_utmos"] = f"{d['pre_mean']:.4f}" if d["pre_mean"] is not None else ""
        r["ref_utmos"] = f"{d['ref']:.4f}" if d["ref"] is not None else ""
    with open(csv_path, "w", newline="") as f:
        w = csv.DictWriter(f, fieldnames=fields)
        w.writeheader()
        w.writerows(rows)
    print("updated", csv_path)


if __name__ == "__main__":
    main()
