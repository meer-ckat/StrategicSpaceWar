# SupertonicTTS Voice Style Extractor

This repo will guide you to extract voice style embeddings from any WAV file for SupertonicTTS,
without the official (unreleased) style encoder.

Put in a 3~10 second voice sample, and get a voice style JSON that makes SupertonicTTS speak in that voice.

**Requirements:** NVIDIA GPU with 4GB+ VRAM, CUDA support.

## Responsible Use

**This is research code released for academic purposes only.** Voice cloning technology can be misused for serious harm. By using this repository, you agree to the following.

- **Obtain explicit consent** from any speaker whose voice you intend to clone. Cloning a real person's voice without permission may be illegal in your jurisdiction.
- **Do not use this tool** for non-consensual voice impersonation, voice phishing (vishing), fraud, harassment, defamation, generation of misleading political or commercial content, or circumvention of voice-based authentication.
- **Do not target identifiable individuals** (including public figures) without their explicit permission.
- **Disclose synthetic audio** as AI-generated when distributing it, ideally with a watermark or provenance metadata (e.g., C2PA).

The author disclaims all liability for misuse. Evaluation in the accompanying paper uses VCTK (CC BY 4.0) and the Common Voice subset of Seed-TTS Eval (CC0), does not target identifiable individuals, and releases no extracted style vector for any real person. If you observe misuse of this code, please open an issue.

### How it works:
```
              ┌───────────────────────────────────────────┐
              │           TTS Pipeline (PyTorch)          │
┌───────────┐ │ ┌─────────┐  ┌───────────┐  ┌───────────┐ │ ┌─────────┐
│   style   │→│ │  Text   │→ │  Vector   │→ │  Vocoder  │ │→│ gen WAV │
│  vector   │ │ │ Encoder │  │ Estimator │  │           │ │ └────┬────┘
└─────┬─────┘ │ └─────────┘  └───────────┘  └───────────┘ │      │
      │       └───────────────────────────────────────────┘      │
      │                                                          │
      │                      ┌────────────┐                      │
      │                      │   WavLM    │◄─────────────────────┘
      │                      │  Layer 4   │
      │                      │  (speaker  │◄── target WAV
      │                      │ identity)  │
      │                      └─────┬──────┘
      │                            │
      │      gradient              │ loss
      └────────────────────────────┘
      "update style to be more similar"
```

1. Auto-selects the closest preset style (F1~F5, M1~M5) via WavLM layer-4 distance
2. Synthesizes WAV via TTS, compares with target WAV using WavLM layer-4 features
3. Updates style vector via gradient descent until convergence (early stop at 0.30)

### Convergence Guide:
Optimization stops at a loss of **0.30**. That number is not arbitrary: synthesizing two
different sentences from the *same* preset leaves a residual loss caused by content rather
than by speaker mismatch, and across the ten presets that floor spans **0.15 to 0.41**
(mean 0.27) at layer 4. Stopping at 0.30 puts the residual below 62% of genuine
same-voice pairs.

The floor is layer-specific, so changing `--layer` requires re-running
`src/calibrate_threshold.py` before trusting any threshold.

Pushing further hurts. Continuing to 0.24 raises ECAPA similarity by 0.036 but drives
mean WER from 1.1% to 5.2%, and WavLM-SV similarity slightly *down* — speaker matching
and intelligibility trade off against each other past this point.

## Quick Start

### 1. Install dependencies
```bash
pip install -r requirements.txt
```

### 2. Download SupertonicTTS models
Download the `onnx/` and `voice_styles/` folders from [Supertone/supertonic-2](https://huggingface.co/Supertone/supertonic-2) and place them under `models/`.

### 3. Prepare your voice sample
Put your WAV files (3~16 seconds each, one speaker per file) in `wavs/`. Any sample rate
works; they are resampled to 44.1 kHz automatically.

### 4. Extract
```bash
python src/run_batch_extract.py --speakers seedtts --limit 1 --save-wav --out results/mine
```
This is the single entry point, for one voice or for hundreds. It picks the closest of
the ten released presets as a starting point, optimizes the style vector, and stops at the
calibrated threshold.

| Flag | Meaning |
|------|---------|
| `--layer` | WavLM layer for the objective (default 4) |
| `--threshold` | early-stopping loss (default 0.30; re-calibrate if you change the layer) |
| `--batch` | speakers optimized simultaneously (default 8, max ~16 on 24 GB) |
| `--limit` | cap the number of speakers |
| `--save-wav` | also write `ref.wav` plus five synthesized prompts per speaker |
| `--resume-from` | continue a previous run at a stricter threshold |
| `--max-steps` / `--plateau` | step cap, and give-up point after no improvement |

### 5. Use the extracted style
Each speaker gets `results/mine/styles/<id>.json`, in the same format SupertonicTTS
consumes. See `legacy/main.py` for an inference example.

## Extracting many voices at once

The same driver optimizes several style vectors in a single forward pass, so a corpus
costs little more than a handful of speakers:

```bash
python src/run_batch_extract.py --speakers seedtts --layer 4 --threshold 0.30 \
                               --batch 16 --save-wav --out results/my_run
```

Each speaker's style vector only reaches its own output, so stacking B speakers and
summing their per-speaker losses gives every vector exactly the gradient it would have
received alone. The loss is summed rather than averaged, which keeps the effective
learning rate per speaker identical to a solo run. All four ONNX graphs declare a dynamic
batch dimension, and every speaker uses the same prompt on a given step, so no padding is
needed.

Measured on an RTX 3090 (24 GB), 5.4 s utterances:

| batch | s/speaker/step | speedup | peak VRAM |
|-------|---------------|---------|-----------|
| 1     | 0.465         | 1.0x    | 2.5 GB    |
| 4     | 0.141         | 3.3x    | 5.6 GB    |
| 8     | 0.090         | 5.2x    | 9.8 GB    |
| 16    | 0.069         | 6.7x    | 18.0 GB   |
| 24    | 0.337         | 1.4x    | 26.1 GB (exceeds VRAM, thrashes) |

Roughly 1.03 GB per additional speaker. **16 is the practical maximum on 24 GB**; going
higher buys little because the GPU is already saturated. This turns a 110-speaker run from
about five hours into about one.

Two details the batching has to respect, both handled by the driver:

- A speaker that reaches the threshold stops being updated **immediately**. Left in the
  batch it would keep descending past the threshold and drift into territory the TTS was
  never trained on.
- Finished speakers are replaced by queued ones so the batch stays full, and the prompt
  rotation runs off a global counter so backfilling does not disturb it.

Output is one JSON per input audio file under `<out>/styles/`. With `--save-wav` each speaker also gets a folder containing
`ref.wav` (the original) alongside the five synthesized prompts.

To continue an existing run at a stricter threshold instead of starting over:

```bash
python src/run_batch_extract.py --speakers seedtts --layer 4 --threshold 0.24 \
                               --resume-from results/my_run --out results/my_run_t024
```

## How long does it take?
1. Model loading & conversion (~30 seconds)
2. Auto style selection (~1 minute, 10 styles compared)
3. Optimization: ~3 minutes per speaker solo, or ~0.5 minutes per speaker at batch 16

## Performance
Evaluated on 154 speakers (110 VCTK + 44 Seed-TTS Common Voice) × 5 utterances = 770 samples:

| | SIM (WavLM) ↑ | SIM (ECAPA) ↑ | SIM (ResNet) ↑ | WER ↓ | UTMOS ↑ |
|---|---|---|---|---|---|
| Nearest preset (no opt.) | 0.713 | 0.132 | 0.099 | 1.84% | 4.47 |
| **Proposed method** | **0.836** | **0.413** | **0.401** | 3.19% | 4.23 |

Every speaker improves over its preset baseline under ECAPA and ResNet (154/154), and
145/154 under WavLM-SV; Wilcoxon signed-rank p < 1e-24 on all three, paired effect size
d_z = 2.70 / 2.82 / 1.32 (ECAPA / ResNet / WavLM-SV).

The WER mean sits on a long tail: the median is 0.00% and 107 of the 154 speakers
transcribe with no error at all. See the caveat below.

UTMOS is a *predicted* MOS, not a listening test, and it was trained on systems from
earlier Blizzard and Voice Conversion Challenges, so absolute values here are out of
domain. The useful reading is the paired one: the presets are style vectors the model
shipped, so 4.23 against 4.47 means an extracted vector synthesizes at 95% of the level
the model reaches from its own vectors, i.e. it lands inside the region the model was
trained on. The median speaker gives up 0.10, at effect size d_z = 0.66 against 2.70 for
the similarity gain on the same clips. (Real reference recordings score 3.90 under this
predictor — it rates clean synthesis above natural speech, which is why the preset
comparison, not the reference, is the anchor.)

For scale, ECAPA cosine similarity on this data runs from **0.118** between unrelated voices
to **0.682** between two different recordings of one speaker (0.804 between two halves of a
single recording, which also fixes the channel). The nearest preset sits at 0.132, essentially
at the impostor floor; the extracted style reaches about 60% of the two-recording level.
Almost all of the gain comes from the optimization, not from starting near a preset.

Reproduce with `src/measure_all.py`, `src/eval_simw.py`, `src/eval_utmos.py`,
`src/speaker_ceiling.py`, `src/verification_operating_point.py` and `src/compute_stats.py`.

### A caveat on WER

SupertonicTTS drops and repeats words sporadically on its own, which the vendor has
[acknowledged](https://github.com/supertone-inc/supertonic/issues/83) and addressed in
Supertonic 3. Measuring the stock presets with no optimization at all, over 250 clips:
mean WER 0.38%, but 0.8% of clips exceed 10% WER, and the same preset and sentence can
swing by 15 points across noise seeds. Any single-clip WER from this model is therefore
unreliable, and a per-speaker WER over five clips still carries real variance. Run
`src/control_wer.py` to reproduce that baseline.

## File Structure

All scripts are run from the repository root, e.g. `python src/run_batch_extract.py ...`.

```
src/                      # active code
├── core.py                 ONNX->PyTorch conversion, WavLM objective, forward pass,
│                          prompts, style I/O, metrics - shared by every script below
├── helper.py               TTS wrapper and style-JSON I/O
├── run_batch_extract.py    batched multi-speaker extraction (main entry point)
├── prepare_vctk.py         VCTK speaker selection
├── measure_all.py          ECAPA + ResNet similarity, WER, preset-gender accuracy
├── eval_simw.py            WavLM-SV similarity
├── eval_utmos.py           predicted naturalness (UTMOS), preset vs extracted
├── eval_batch.py           single-run ECAPA similarity + WER
├── calibrate_threshold.py  per-layer early-stopping threshold
├── speaker_ceiling.py      empirical same-speaker ceiling / different-speaker floor
├── verification_operating_point.py  EER and FAR acceptance rates
├── control_wer.py          stock-preset WER baseline (model's own instability)
├── diagnose_sim.py         similarity decomposition (real/synth domain gap)
├── make_figs.py            manuscript figures
└── compute_stats.py        significance tests and bootstrap CIs

models/                   # obtained from HuggingFace Supertone/supertonic-2
├── onnx/                   duration_predictor, text_encoder, vector_estimator,
│                           vocoder, tts.json, unicode_indexer.json
└── voice_styles/           M1-M5.json, F1-F5.json (the 10 released presets)

data/                     # corpora (not tracked)
wavs/                     # Seed-TTS Eval reference audio

results/                  # not tracked
├── main_layer4/            the 154-speaker run behind every number above
├── pilot_layer4/           layer-4 pilots (thr030, thr024, control_wer)
├── _layer3_seedtts/        superseded layer-3 run; two Discussion pilots still cite it
├── threshold_calibration.json
└── speaker_ceiling.json

legacy/                   # superseded scripts, kept for provenance only
paper/                    # manuscript, ICASSP style files, figures
```

## Models Used

| Model | Role |
|-------|------|
| duration_predictor | Duration prediction (SupertonicTTS) |
| text_encoder | Text encoding (SupertonicTTS) |
| vector_estimator | Flow matching denoising (SupertonicTTS) |
| vocoder | Latent to WAV (SupertonicTTS) |
| WavLM-Large | Perceptual loss, layer 4 (microsoft/wavlm-large) |

## Technical Details

### ONNX to PyTorch Conversion
ONNX models are converted to PyTorch for gradient backpropagation:
- onnxslim for model cleanup
- Forced opset 17 (onnx2torch compatibility)
- Clip node empty input fix

### WavLM layer-4 feature matching
[Chiu et al. (2025)](https://arxiv.org/abs/2501.05310) report that for WavLM-Large,
speaker-identification accuracy peaks at **layer 4** (gender at layer 2, pitch at layer 1),
so layer 4 is what we use. Their layer 0 is the CNN encoder output, which matches
HuggingFace's `hidden_states` indexing, so `hidden_states[4]` is their layer 4.

We compare time-averaged feature statistics (mean and standard deviation over the time
axis) between generated and target audio. Collapsing the time axis is what makes the
objective content-independent: the two clips can say different words. It is also the same
statistics pooling that x-vector speaker embeddings use, which is why the objective
transfers to speaker-verification metrics it never saw during optimization.

### Style Space
- `style_ttl` [1, 50, 256] = 12,800 parameters (timbre, optimized)
- `style_dp` [1, 8, 16] = 128 parameters (rhythm/duration, frozen)

Male and female voices occupy different regions of the style space, and starting from the
nearest preset converges fastest. It is a convenience rather than a requirement, though:
a zero-vector start still reaches ECAPA 0.50 against 0.59 for the preset start, and even
starting from a preset of the opposite vocal category reaches 0.46 — all far above the
0.13 preset baseline. The optimizer recovers from a poor start rather than failing.

## Citation

If you use this work, please cite:

```bibtex
@misc{kim2026supertonicembed,
  author        = {Gyeongmin Kim},
  title         = {Extracting Voice Styles from Frozen {TTS} Models via Gradient-Based Inverse Optimization},
  year          = {2026},
  eprint        = {2607.25351},
  archivePrefix = {arXiv},
  primaryClass  = {eess.AS},
  url           = {https://arxiv.org/abs/2607.25351}
}
```

Preprint: https://arxiv.org/abs/2607.25351

## Looking for help?
If you have any questions, please feel free to open an issue.
