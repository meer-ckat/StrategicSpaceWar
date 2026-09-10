"""How often does the stock model misread, with no style optimization at all?

Our threshold comparisons hold the seed and the latent fixed and change only the
style vector, so a WER jump there looks like optimization damage. But
SupertonicTTS drops and repeats words on its own - the vendor acknowledged this
and fixed it in Supertonic 3 - so a baseline failure rate is needed before any
WER number can be attributed to the method. This measures that baseline: the
released presets, unmodified, across several prompts and several noise seeds.

Usage:  python src/control_wer.py --seeds 5
"""
import argparse
import json
import os

import numpy as np
import soundfile as sf
import torch

from core import (SR, TEXTS, encode_text, load_pipeline, preset_paths,
                  preset_tensors, tts_forward, wer, SPEED, TOTAL_STEP)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--seeds", type=int, default=5)
    ap.add_argument("--out", default="results/pilot_layer4/control_wer")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    models = load_pipeline()
    tts, dp, te, ve, voc = models
    from faster_whisper import WhisperModel
    wm = WhisperModel("large-v3", device="cuda", compute_type="float16")

    presets = preset_paths()
    print(f"presets {len(presets)}  texts {len(TEXTS)}  seeds {args.seeds}  "
          f"= {len(presets) * len(TEXTS) * args.seeds} clips\n")

    chunk = tts.base_chunk_size * tts.chunk_compress_factor
    dim = tts.ldim * tts.chunk_compress_factor
    tmp = f"{args.out}/_tmp.wav"
    rows = []
    for p in presets:
        sttl, sdp = preset_tensors(p)
        name = os.path.basename(p).replace(".json", "")
        for ti, text in enumerate(TEXTS):
            ids, msk = encode_text(tts, text)
            with torch.no_grad():
                d = dp(ids, sdp, msk) / SPEED
            llen = int(np.ceil(int(float(d.item()) * SR) / chunk))
            lmask = torch.ones(1, 1, llen, device=sttl.device)
            # the latent is the ONLY thing varying here, which is exactly the
            # nondeterminism users report
            for seed in range(args.seeds):
                rng = np.random.RandomState(1000 + seed)
                lat = torch.tensor(rng.randn(1, dim, llen).astype(np.float32),
                                   device=sttl.device)
                with torch.no_grad():
                    w, _ = tts_forward(ids, msk, sttl, sdp, dp, te, ve, voc,
                                       TOTAL_STEP, SPEED, lat, lmask)
                sf.write(tmp, w.reshape(-1).cpu().numpy(), SR)
                segs, _ = wm.transcribe(tmp, language="en", beam_size=5)
                rows.append({"preset": name, "text": ti + 1, "seed": seed,
                             "wer": wer(text, " ".join(x.text for x in segs).strip())})
        done = [r for r in rows if r["preset"] == name]
        print(f"  {name}: mean {100 * np.mean([r['wer'] for r in done]):5.2f}%  "
              f"max {100 * max(r['wer'] for r in done):6.2f}%  "
              f"failures>10%: {sum(r['wer'] > .10 for r in done)}/{len(done)}",
              flush=True)
    if os.path.exists(tmp):
        os.remove(tmp)

    w = np.array([r["wer"] for r in rows])
    print("\n" + "=" * 58)
    print(f"stock presets, NO optimization   n={len(w)} clips")
    print("=" * 58)
    print(f"  mean {100 * w.mean():.2f}%   median {100 * np.median(w):.2f}%   "
          f"max {100 * w.max():.2f}%")
    for t in (0.05, 0.10, 0.20):
        print(f"  clips with WER > {int(t * 100):>2}% : {int((w > t).sum()):>3}/{len(w)}"
              f"  ({100 * (w > t).mean():.1f}%)")

    spread = [max(v) - min(v) for p in {r["preset"] for r in rows}
              for t in range(1, len(TEXTS) + 1)
              for v in [[r["wer"] for r in rows
                         if r["preset"] == p and r["text"] == t]] if len(v) > 1]
    print(f"\n  same preset and text, spread across seeds: "
          f"mean {100 * np.mean(spread):.2f}pp   max {100 * max(spread):.2f}pp")
    print("  -> a large spread means the model itself is unstable, so a single")
    print("     WER measurement cannot be attributed to the style vector")

    with open(f"{args.out}/control_wer.json", "w", encoding="utf-8") as f:
        json.dump(rows, f, indent=2)
    print(f"\nwrote {args.out}/control_wer.json")


if __name__ == "__main__":
    main()
