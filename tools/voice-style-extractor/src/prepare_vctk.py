"""Select one reference utterance per VCTK speaker.

VCTK ships gender/accent metadata in speaker-info.txt, which gives us the
independent speaker labels the Seed-TTS evaluation never had. Sentences 001-003
are read by every speaker; 003 in particular is long, so "longest utterance"
tends to select it. That is content-controlled rather than wrong, but it is a
choice, so this script reports how often it happens instead of hiding it.

Outputs data/vctk_speakers.json for the extraction run.
"""
import argparse
import json
import os
import re
import wave
from collections import Counter

import soundfile as sf

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # repo root, one level above src/
VCTK = os.path.join(ROOT, "data", "vctk")
OUT = os.path.join(ROOT, "data", "vctk_speakers.json")
REF_DIR = os.path.join(ROOT, "data", "vctk_refs")

COMMON = {"001", "002", "003"}   # read by every speaker


def parse_speaker_info(path):
    """ID AGE GENDER ACCENTS REGION -- whitespace separated, one header line."""
    info = {}
    with open(path, encoding="utf-8", errors="replace") as f:
        next(f)
        for line in f:
            parts = line.split()
            if len(parts) >= 4:
                info[parts[0]] = {
                    "age": parts[1],
                    "gender": parts[2].upper()[:1],
                    "accent": parts[3],
                    "region": " ".join(parts[4:]) if len(parts) > 4 else "",
                }
    return info


def duration(path):
    try:
        i = sf.info(path)
        return i.frames / i.samplerate
    except Exception:
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mic", default="mic1", choices=["mic1", "mic2"])
    ap.add_argument("--min-dur", type=float, default=3.0)
    ap.add_argument("--max-dur", type=float, default=12.0,
                    help="reject anything longer; guards against outlier reads")
    ap.add_argument("--exclude-common", action="store_true",
                    help="ignore the sentences every speaker reads (001-003)")
    ap.add_argument("--limit", type=int, default=0, help="cap speaker count")
    ap.add_argument("--export", action="store_true",
                    help="write the chosen reference wavs to data/vctk_refs/")
    args = ap.parse_args()

    sinfo = parse_speaker_info(os.path.join(VCTK, "speaker-info.txt"))
    wav_root = os.path.join(VCTK, f"wav48_silence_trimmed")
    if not os.path.isdir(wav_root):
        raise SystemExit(f"not found: {wav_root}\nUnzip VCTK into data/ first.")

    speakers = sorted(d for d in os.listdir(wav_root)
                      if os.path.isdir(os.path.join(wav_root, d)))
    print(f"speakers on disk: {len(speakers)}   metadata rows: {len(sinfo)}\n")

    chosen, skipped, picked_common = [], [], 0
    for spk in speakers:
        d = os.path.join(wav_root, spk)
        cands = []
        for fn in os.listdir(d):
            if not fn.endswith(".flac") or args.mic not in fn:
                continue
            m = re.search(r"_(\d{3})_", fn)
            sid = m.group(1) if m else ""
            if args.exclude_common and sid in COMMON:
                continue
            p = os.path.join(d, fn)
            dur = duration(p)
            if dur is None or not (args.min_dur <= dur <= args.max_dur):
                continue
            cands.append((dur, sid, p))
        if not cands:
            skipped.append(spk)
            continue
        dur, sid, path = max(cands)
        if sid in COMMON:
            picked_common += 1
        meta = sinfo.get(spk, {})
        chosen.append({
            "spk": spk,
            "gender": meta.get("gender", "?"),
            "accent": meta.get("accent", "?"),
            "age": meta.get("age", "?"),
            "sentence_id": sid,
            "dur": round(dur, 2),
            "path": os.path.relpath(path, ROOT).replace("\\", "/"),
        })

    if args.limit:
        # keep the gender balance when capping
        f = [c for c in chosen if c["gender"] == "F"]
        m = [c for c in chosen if c["gender"] == "M"]
        half = args.limit // 2
        chosen = f[:half] + m[:args.limit - half]

    durs = sorted(c["dur"] for c in chosen)
    g = Counter(c["gender"] for c in chosen)
    acc = Counter(c["accent"] for c in chosen)
    print(f"selected {len(chosen)} speakers   ({g['F']}F / {g['M']}M)")
    if skipped:
        print(f"skipped {len(skipped)} with no utterance in "
              f"[{args.min_dur}, {args.max_dur}]s: {skipped[:6]}")
    if durs:
        n = len(durs)
        print(f"duration  min {durs[0]:.2f}  p25 {durs[n//4]:.2f}  "
              f"median {durs[n//2]:.2f}  p75 {durs[3*n//4]:.2f}  max {durs[-1]:.2f}")
        print(f"in 6-8 s window: {sum(1 for d in durs if 6 <= d <= 8)}/{n}")
    print(f"picked a common sentence (001-003): {picked_common}/{len(chosen)}"
          + ("  <- content is controlled across speakers" if picked_common > len(chosen) * 0.5 else ""))
    print("accents:", ", ".join(f"{k}:{v}" for k, v in acc.most_common(8)))

    if args.export:
        os.makedirs(REF_DIR, exist_ok=True)
        import librosa
        for c in chosen:
            y, _ = librosa.load(os.path.join(ROOT, c["path"]), sr=44100)
            dest = os.path.join(REF_DIR, f"{c['spk']}.wav")
            sf.write(dest, y, 44100)
            c["ref_wav"] = os.path.relpath(dest, ROOT).replace("\\", "/")
        print(f"exported {len(chosen)} reference wavs to {REF_DIR}")

    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(chosen, f, indent=2)
    print(f"wrote {OUT}")


if __name__ == "__main__":
    main()
