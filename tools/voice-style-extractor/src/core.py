"""Shared machinery for style extraction from a frozen SupertonicTTS.

This module holds everything the scripts have in common: loading the released
ONNX pipeline as differentiable PyTorch, the WavLM objective, the rotating
prompt set with its fixed latents, and the metrics. Scripts here are drivers;
they should not re-implement any of it.

The TTS itself is never trained. `load_pt_model` freezes every converted
parameter, and only a style vector is ever handed to an optimizer.
"""

import json
import os

import numpy as np
import onnx
import onnx2torch
import onnxslim
import torch
import torch.nn.functional as F
import torchaudio
from onnx import shape_inference
from onnx2torch import convert

from helper import load_text_to_speech, load_voice_style

# HuggingFace downloads fail behind the local TLS interception, so verification
# is disabled for the client they use.
os.environ.pop("SSL_CERT_FILE", None)
os.environ.pop("CURL_CA_BUNDLE", None)
os.environ.pop("REQUESTS_CA_BUNDLE", None)
import httpx  # noqa: E402

_orig_client = httpx.Client


class _NoVerifyClient(_orig_client):
    def __init__(self, *args, **kwargs):
        kwargs["verify"] = False
        super().__init__(*args, **kwargs)


httpx.Client = _NoVerifyClient

DEVICE = torch.device("cuda" if torch.cuda.is_available() else "cpu")

SR = 44100                 # SupertonicTTS output rate
WAVLM_SR = 16000
LAYER = 4                  # peak speaker-identification layer for WavLM-Large
THRESHOLD = 0.30           # same-preset loss floor at that layer; see calibrate_threshold.py
TOTAL_STEP = 5             # flow-matching sampling steps
SPEED = 1.05
SEED = 42
LR = 2e-4

# Rotated across optimization steps so a style vector cannot overfit one
# phoneme set. Every script that synthesizes for evaluation uses this same set.
TEXTS = [
    "The sun sets behind the mountains, painting the sky in shades of pink and orange.",
    "Technology has transformed the way we communicate with each other around the world.",
    "She walked through the quiet forest, listening to the birds singing in the trees.",
    "Scientific research requires patience, careful observation, and rigorous analysis.",
    "The old library contained thousands of books collected over many generations.",
]


# ===== ONNX to PyTorch =====

def _patch_onnx2torch():
    """Bypass onnx2torch's safe_shape_inference, which writes temp files."""
    def patched(m):
        if isinstance(m, str):
            m = onnx.load(m)
        try:
            return shape_inference.infer_shapes(m)
        except Exception:
            return m
    onnx2torch.converter.safe_shape_inference = patched


def _fix_clip(model):
    """Drop trailing empty Clip inputs, which onnx2torch cannot convert."""
    for node in model.graph.node:
        if node.op_type == "Clip":
            inputs = list(node.input)
            while inputs and inputs[-1] == "":
                inputs.pop()
            del node.input[:]
            node.input.extend(inputs)
    return model


def load_pt_model(name, onnx_dir="models/onnx"):
    """Slim one released ONNX module, drop it to opset 17, convert, and freeze."""
    slimmed = onnxslim.slim(os.path.join(onnx_dir, name))
    for opset in slimmed.opset_import:
        if opset.domain in ("", "ai.onnx"):
            opset.version = 17
    _fix_clip(slimmed)
    m = convert(slimmed)
    m.eval()
    for p in m.parameters():
        p.requires_grad_(False)
    return m.to(DEVICE)


def load_pipeline(onnx_dir="models/onnx"):
    """The text processor plus the four differentiable, frozen TTS modules."""
    tts = load_text_to_speech(onnx_dir)
    return (tts,
            load_pt_model("duration_predictor.onnx", onnx_dir),
            load_pt_model("text_encoder.onnx", onnx_dir),
            load_pt_model("vector_estimator.onnx", onnx_dir),
            load_pt_model("vocoder.onnx", onnx_dir))


# ===== WavLM objective =====

def load_wavlm():
    """Load WavLM-Large, frozen.

    Chiu et al. (2025) report peak speaker-identification accuracy at layer 4
    for this model. Their layer 0 is the CNN encoder output, which matches
    HuggingFace's `hidden_states` indexing, so `hidden_states[4]` is their
    layer 4.
    """
    from transformers import WavLMModel
    model = WavLMModel.from_pretrained("microsoft/wavlm-large").to(DEVICE).eval()
    for p in model.parameters():
        p.requires_grad_(False)
    return model


def to16k(wav):
    """Resample to WavLM's rate, differentiably, keeping the batch dimension."""
    if wav.ndim == 1:
        wav = wav.unsqueeze(0)
    n = int(round(wav.shape[-1] * WAVLM_SR / SR))
    return F.interpolate(wav.unsqueeze(1), size=n, mode="linear",
                         align_corners=False).squeeze(1)


def wav_stats(wavlm, wav16, layer=LAYER):
    """Per-dimension mean and standard deviation over time of one WavLM layer.

    Collapsing the time axis is what makes the objective content-independent:
    two clips may say different words. It is also the statistics pooling used by
    x-vector speaker embeddings, which is why the objective transfers to speaker
    verification metrics it never saw.
    """
    h = wavlm(wav16, output_hidden_states=True).hidden_states[layer]
    return h.mean(dim=1), h.std(dim=1)


def style_loss(gen_stats, tgt_stats):
    """Mean squared error over the D feature dimensions, on mean and on std."""
    return (F.mse_loss(gen_stats[0], tgt_stats[0])
            + F.mse_loss(gen_stats[1], tgt_stats[1]))


def target_stats_from_file(wavlm, path, layer=LAYER):
    """Layer statistics of a reference recording, at any input sample rate."""
    import librosa
    y, _ = librosa.load(path, sr=WAVLM_SR)
    t = torch.tensor(y, dtype=torch.float32, device=DEVICE).unsqueeze(0)
    with torch.no_grad():
        return wav_stats(wavlm, t, layer)


def wavlm_feature_loss(wavlm, gen_wav, target_features, layer=LAYER):
    """Loss between a generated waveform at 44.1 kHz and precomputed targets."""
    return style_loss(wav_stats(wavlm, to16k(gen_wav), layer), target_features)


def extract_wavlm_targets(wavlm, target_wav, layer=LAYER):
    """Layer statistics of a 44.1 kHz reference already in memory."""
    if target_wav.ndim == 1:
        target_wav = target_wav.unsqueeze(0)
    wav_16k = torchaudio.functional.resample(target_wav, SR, WAVLM_SR)
    with torch.no_grad():
        return wav_stats(wavlm, wav_16k, layer)


# ===== TTS forward =====

def tts_forward(text_ids, text_mask, style_ttl, style_dp,
                dp_model, te_model, ve_model, voc_model,
                total_step, speed, noisy_latent, latent_mask):
    """Differentiable pass through all four modules.

    Handles any batch size: each style vector reaches only its own output, so a
    batch of B speakers is equivalent to B separate runs.
    """
    b = style_ttl.shape[0]
    dur = dp_model(text_ids, style_dp, text_mask) / speed
    text_emb = te_model(text_ids, style_ttl, text_mask)
    xt = noisy_latent * latent_mask
    total_t = torch.full((b,), float(total_step), device=DEVICE)
    for step in range(total_step):
        cur_t = torch.full((b,), float(step), device=DEVICE)
        xt = ve_model(xt, text_emb, style_ttl, latent_mask, text_mask, cur_t, total_t)
    return voc_model(xt), dur


# ===== Prompts and their fixed latents =====

def encode_text(tts, text, lang="en"):
    """Token ids and mask, shaped for a batch of one."""
    ids_np, msk_np = tts.text_processor(text, lang)
    ids = torch.tensor(ids_np, dtype=torch.int64, device=DEVICE)
    msk = torch.tensor(msk_np, dtype=torch.float32, device=DEVICE)
    if ids.dim() == 1:
        ids = ids.unsqueeze(0)
    if msk.dim() == 2:
        msk = msk.unsqueeze(1)
    return ids, msk


def build_prompts(tts, dp_model, texts=TEXTS, seed=SEED, speed=SPEED,
                  ref_style="models/voice_styles/M1.json"):
    """One fixed noise latent per prompt, sized from that prompt's duration.

    The latent is drawn once from a fixed seed and reused at every step, so the
    only thing that changes during optimization is the style vector. Sizing it
    per prompt keeps the optimization forward pass consistent with inference,
    where the latent length follows the predicted duration.
    """
    torch.manual_seed(seed)
    np.random.seed(seed)
    ref = load_voice_style(ref_style)
    dp_ref = torch.tensor(ref.dp, dtype=torch.float32, device=DEVICE)
    chunk = tts.base_chunk_size * tts.chunk_compress_factor
    dim = tts.ldim * tts.chunk_compress_factor

    prompts = []
    for text in texts:
        ids, msk = encode_text(tts, text)
        with torch.no_grad():
            d = dp_model(ids, dp_ref, msk) / speed
        llen = int(np.ceil(int(float(d.item()) * SR) / chunk))
        prompts.append({
            "text": text,
            "ids": ids,
            "msk": msk,
            "lat": torch.tensor(
                np.random.randn(1, dim, llen).astype(np.float32), device=DEVICE),
            "lmask": torch.ones(1, 1, llen, device=DEVICE),
        })
    return prompts


def synthesize(prompt, style_ttl, style_dp, models, total_step=TOTAL_STEP,
               speed=SPEED):
    """One prompt through the pipeline with a given style. Returns a 1-D waveform."""
    _, dp, te, ve, voc = models
    with torch.no_grad():
        wav, _ = tts_forward(prompt["ids"], prompt["msk"], style_ttl, style_dp,
                             dp, te, ve, voc, total_step, speed,
                             prompt["lat"], prompt["lmask"])
    return wav.reshape(-1)


# ===== Style I/O =====

def save_style(path, style_ttl, style_dp, source_file=None):
    """Write a style vector in the JSON format SupertonicTTS itself consumes."""
    from datetime import datetime
    payload = {
        "style_ttl": {"data": style_ttl.detach().cpu().numpy().tolist(),
                      "dims": [1, 50, 256], "type": "float32"},
        "style_dp": {"data": style_dp.detach().cpu().numpy().tolist(),
                     "dims": [1, 8, 16], "type": "float32"},
        "metadata": {"source_file": source_file or "unknown",
                     "source_sample_rate": SR, "target_sample_rate": SR,
                     "extracted_at": datetime.now().isoformat()},
    }
    d = os.path.dirname(path)
    if d:
        os.makedirs(d, exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(payload, f)


def load_style_tensors(path):
    """Read a style JSON back into (style_ttl, style_dp)."""
    with open(path, encoding="utf-8") as f:
        d = json.load(f)
    return (torch.tensor(d["style_ttl"]["data"], dtype=torch.float32,
                         device=DEVICE).reshape(1, 50, 256),
            torch.tensor(d["style_dp"]["data"], dtype=torch.float32,
                         device=DEVICE).reshape(1, 8, 16))


def preset_paths(pattern="models/voice_styles/[FM]*.json"):
    import glob
    return sorted(glob.glob(pattern))


def preset_tensors(path):
    st = load_voice_style(path)
    return (torch.tensor(st.ttl, dtype=torch.float32, device=DEVICE),
            torch.tensor(st.dp, dtype=torch.float32, device=DEVICE))


# ===== Metrics =====

def normalize_text(s):
    import re
    return re.sub(r"[^a-z0-9' ]", " ", s.lower()).split()


def wer(reference, hypothesis):
    """Word error rate as edit distance over words: (S + D + I) / N."""
    r, h = normalize_text(reference), normalize_text(hypothesis)
    d = [[0] * (len(h) + 1) for _ in range(len(r) + 1)]
    for i in range(len(r) + 1):
        d[i][0] = i
    for j in range(len(h) + 1):
        d[0][j] = j
    for i in range(1, len(r) + 1):
        for j in range(1, len(h) + 1):
            d[i][j] = min(d[i - 1][j] + 1,
                          d[i][j - 1] + 1,
                          d[i - 1][j - 1] + (r[i - 1] != h[j - 1]))
    return d[len(r)][len(h)] / max(1, len(r))
