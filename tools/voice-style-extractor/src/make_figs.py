"""Regenerate the paper's results figure from a completed run.

Two panels, one two-column-wide figure:
  (a) best-so-far loss for every speaker, against the stopping threshold
  (b) ECAPA similarity before and after extraction

ICASSP requires >=9 pt type everywhere including captions, and figures must stay
readable printed black-only. The figure is generated at its final printed size
(7 in = two columns) so LaTeX never rescales the fonts.

Usage:  python src/make_figs.py results/main_layer4
"""
import argparse
import csv
import glob
import json
import os

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
import numpy as np

OUT = "paper"

plt.rcParams.update({
    "font.family": "serif",
    "font.serif": ["STIXGeneral", "DejaVu Serif"],
    "mathtext.fontset": "stix",
    # IEEE PDF eXpress rejects Type 3; 42 embeds TrueType instead.
    "pdf.fonttype": 42, "ps.fonttype": 42,
    "font.size": 9, "axes.labelsize": 9, "axes.titlesize": 9,
    "xtick.labelsize": 9, "ytick.labelsize": 9, "legend.fontsize": 9,
    "axes.linewidth": 0.6,
    "xtick.major.width": 0.6, "ytick.major.width": 0.6,
    "xtick.major.size": 2.5, "ytick.major.size": 2.5,
})

INK, MUTED, GRID = "#1a1a1a", "#9aa0a6", "#e3e5e8"
ACCENT, BASELINE, BASELINE_EDGE = "#2b6cb0", "#c9cdd2", "#7d8288"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("run", nargs="?", default="results/main_layer4")
    ap.add_argument("--dest", default=f"{OUT}/results_fig")
    args = ap.parse_args()

    summary = json.load(open(f"{args.run}/summary.json", encoding="utf-8"))
    thr, layer = summary["threshold"], summary["layer"]

    hists = []
    for p in sorted(glob.glob(f"{args.run}/*_loss.json")):
        h = json.load(open(p))
        if isinstance(h, list) and h:
            hists.append(np.minimum.accumulate(np.asarray(h, float)))

    rows = list(csv.DictReader(open(f"{args.run}/metrics.csv", newline="",
                                    encoding="utf-8")))
    pre = np.array([float(r["pre_ecapa"]) for r in rows])
    opt = np.array([float(r["opt_ecapa"]) for r in rows])
    print(f"{len(hists)} loss curves, {len(rows)} speakers")

    fig, (ax1, ax2) = plt.subplots(1, 2, figsize=(7.0, 2.05))

    # ---- (a) convergence ------------------------------------------------
    for h in hists:
        ax1.plot(h, color=MUTED, lw=0.4, alpha=0.28, solid_capstyle="round")
    maxlen = max(len(h) for h in hists)
    stack = np.full((len(hists), maxlen), np.nan)
    for i, h in enumerate(hists):
        stack[i, :len(h)] = h
        stack[i, len(h):] = h[-1]          # hold after early stop
    mean_curve = np.nanmean(stack, axis=0)
    ax1.plot(mean_curve, color=ACCENT, lw=1.6, solid_capstyle="round", zorder=5)
    ax1.axhline(thr, color=INK, lw=0.9, ls=(0, (4, 2)), zorder=4)
    ax1.text(maxlen * 0.98, thr + 0.02, f"stopping threshold {thr:g}",
             ha="right", va="bottom", fontsize=9, color=INK)
    k = int(len(mean_curve) * 0.4)
    ax1.text(k, mean_curve[k] + 0.05, "mean", color=ACCENT, fontsize=9,
             ha="left", va="bottom")
    ax1.set_xlabel("optimization step")
    ax1.set_ylabel(f"WavLM layer-{layer} loss")
    ax1.set_xlim(0, maxlen)
    ax1.set_ylim(thr - 0.1, 1.05)
    ax1.set_title(f"(a) convergence, {len(hists)} speakers", pad=4)

    # ---- (b) similarity -------------------------------------------------
    # Dodged bars, not overlaid translucent histograms: overlap regions in an
    # alpha-blended histogram are ambiguous and worse once printed black-only.
    bins = np.linspace(0, 0.75, 16)
    h_pre, _ = np.histogram(pre, bins=bins)
    h_opt, _ = np.histogram(opt, bins=bins)
    centers = (bins[:-1] + bins[1:]) / 2
    bw = (bins[1] - bins[0]) * 0.42
    ax2.bar(centers - bw / 2, h_pre, width=bw * 0.92, color=BASELINE,
            edgecolor=BASELINE_EDGE, lw=0.5, label="nearest preset", zorder=2)
    ax2.bar(centers + bw / 2, h_opt, width=bw * 0.92, color=ACCENT,
            edgecolor="none", label="proposed", zorder=2)
    ax2.set_xlabel(r"SIM$_\mathrm{E}$  (ECAPA-TDNN)")
    ax2.set_ylabel("speakers")
    ax2.set_xlim(0, 0.75)
    ax2.set_title(f"(b) speaker similarity, {len(rows)} speakers", pad=4)
    leg = ax2.legend(frameon=False, loc="upper right", handlelength=1.1,
                     borderpad=0.2, labelspacing=0.3)
    for t in leg.get_texts():
        t.set_color(INK)

    for ax in (ax1, ax2):
        ax.grid(True, color=GRID, lw=0.5, zorder=0)
        ax.set_axisbelow(True)
        for side in ("top", "right"):
            ax.spines[side].set_visible(False)
        for side in ("left", "bottom"):
            ax.spines[side].set_color(MUTED)
        ax.tick_params(colors=INK, labelcolor=INK)

    fig.tight_layout(pad=0.4, w_pad=1.6)
    os.makedirs(os.path.dirname(args.dest) or ".", exist_ok=True)
    fig.savefig(f"{args.dest}.pdf", bbox_inches="tight", pad_inches=0.02)
    fig.savefig(f"{args.dest}.png", dpi=220, bbox_inches="tight", pad_inches=0.02)
    print(f"wrote {args.dest}.pdf")

    steps = [len(h) for h in hists]
    print(f"steps  mean {np.mean(steps):.0f}  range {min(steps)}-{max(steps)}")
    print(f"SIM_E  preset {pre.mean():.3f}  proposed {opt.mean():.3f}")


if __name__ == "__main__":
    main()
