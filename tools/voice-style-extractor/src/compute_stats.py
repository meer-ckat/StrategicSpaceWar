"""Recompute the paper's statistics from the 154-speaker run.

Answers: (1) is the preset -> proposed improvement statistically significant?
         (2) do the paper's reported numbers reproduce from the run's metrics.csv?

Reads results/main_layer4/metrics.csv, the output of run_batch_extract.py
followed by measure_all.py, eval_simw.py and eval_utmos.py. The superseded
layer-3 run in results/_layer3_seedtts is not used here; only the two
Discussion pilots in the paper cite it, with their own settings stated in text.
"""
import csv
import os

import numpy as np
from scipy import stats

RNG = np.random.default_rng(0)
B = 20000
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))  # repo root, one level above src/
RUN = "results/main_layer4"


def load(path):
    with open(f"{ROOT}/{path}", newline="", encoding="utf-8") as f:
        return list(csv.DictReader(f))


def boot_ci_mean(x, b=B, alpha=0.05):
    x = np.asarray(x, float)
    idx = RNG.integers(0, len(x), size=(b, len(x)))
    means = x[idx].mean(axis=1)
    return np.percentile(means, [100 * alpha / 2, 100 * (1 - alpha / 2)])


def boot_ci_diff(a, b_, b=B, alpha=0.05):
    """Paired bootstrap CI of mean(a - b_)."""
    d = np.asarray(a, float) - np.asarray(b_, float)
    idx = RNG.integers(0, len(d), size=(b, len(d)))
    return np.percentile(d[idx].mean(axis=1), [100 * alpha / 2, 100 * (1 - alpha / 2)])


def paired_report(name, opt, pre):
    opt = np.asarray(opt, float)
    pre = np.asarray(pre, float)
    d = opt - pre
    w = stats.wilcoxon(opt, pre)
    t = stats.ttest_rel(opt, pre)
    # paired Cohen's d
    dz = d.mean() / d.std(ddof=1)
    lo, hi = boot_ci_mean(opt)
    dlo, dhi = boot_ci_diff(opt, pre)
    print(f"\n### {name}  (n={len(d)})")
    print(f"  preset   mean = {pre.mean():.4f}  (sd {pre.std(ddof=1):.4f})")
    print(f"  proposed mean = {opt.mean():.4f}  (sd {opt.std(ddof=1):.4f})")
    print(f"  proposed 95% bootstrap CI of mean = [{lo:.3f}, {hi:.3f}]")
    print(f"  improvement  = {d.mean():+.4f}   95% CI [{dlo:+.4f}, {dhi:+.4f}]")
    print(f"  Wilcoxon signed-rank: W={w.statistic:.1f}, p={w.pvalue:.3e}")
    print(f"  paired t-test:        t={t.statistic:.2f},  p={t.pvalue:.3e}")
    print(f"  Cohen's d_z = {dz:.2f}   |  improved in {int((d > 0).sum())}/{len(d)} speakers")
    return dict(mean=opt.mean(), ci=(lo, hi), p=w.pvalue, dz=dz)


rows = load(f"{RUN}/metrics.csv")
vctk = [r for r in rows if r["corpus"] == "vctk"]
seed = [r for r in rows if r["corpus"] == "seedtts"]
assert len(rows) == 154 and len(vctk) == 110 and len(seed) == 44, \
    (len(rows), len(vctk), len(seed))


def col(rs, c):
    return np.array([float(r[c]) for r in rs])


print("=" * 68)
print("PAIRED SIGNIFICANCE TESTS  (preset baseline -> proposed), 154 speakers")
print("=" * 68)

r_w = paired_report("SIM_W  (WavLM-base-plus-sv)", col(rows, "opt_wavlmsv"),
                    col(rows, "pre_wavlmsv"))
r_e = paired_report("SIM_E  (ECAPA-TDNN)", col(rows, "opt_ecapa"),
                    col(rows, "pre_ecapa"))
r_r = paired_report("SIM_R  (ResNet)", col(rows, "opt_resnet"),
                    col(rows, "pre_resnet"))

print("\n" + "=" * 68)
print("PAPER'S REPORTED CIs vs RECOMPUTED BOOTSTRAP CIs")
print("=" * 68)
for label, got, claimed in [
    ("SIM_E", r_e["ci"], "[.395, .430]"),
    ("SIM_R", r_r["ci"], "[.385, .418]"),
]:
    print(f"  {label}: recomputed [{got[0]:.3f}, {got[1]:.3f}]   paper {claimed}")
print("  paper also states p < 1e-26 for both, d_z 2.70 / 2.82, and")
print("  154/154 improved under SIM_E and SIM_R, 145/154 under SIM_W")

print("\n" + "=" * 68)
print("CROSS-METRIC AGREEMENT (architecture independence)")
print("=" * 68)
rho, p = stats.spearmanr(col(rows, "opt_ecapa"), col(rows, "opt_resnet"))
print(f"  Spearman rho(ECAPA, ResNet) on proposed = {rho:.4f}  (p={p:.2e})"
      f"   paper: 0.881")
rho2, p2 = stats.spearmanr(col(rows, "opt_wavlmsv"), col(rows, "opt_ecapa"))
print(f"  Spearman rho(WavLM-SV, ECAPA)           = {rho2:.4f}  (p={p2:.2e})")

print("\n" + "=" * 68)
print("WER")
print("=" * 68)
ow, pw = col(rows, "opt_wer"), col(rows, "pre_wer")
lo, hi = boot_ci_mean(ow)
print(f"  preset   mean = {pw.mean()*100:.2f}%   (paper: 1.84%)")
print(f"  proposed mean = {ow.mean()*100:.2f}%   95% CI [{lo*100:.2f}%, {hi*100:.2f}%]"
      f"   (paper: 3.19%)")
print(f"  proposed median = {np.median(ow)*100:.2f}%   speakers with WER=0: "
      f"{int((ow == 0).sum())}/154   (paper: 0.00%, 107)")

print("\n" + "=" * 68)
print("UTMOS  (predicted naturalness)")
print("=" * 68)
ou, pu = col(rows, "opt_utmos"), col(rows, "pre_utmos")
du = pu - ou
print(f"  preset {pu.mean():.2f} -> extracted {ou.mean():.2f}   "
      f"ratio {ou.mean()/pu.mean()*100:.1f}%   (paper: 4.47 -> 4.23, 95%)")
print(f"  median paired drop = {np.median(du):.2f}   d_z = "
      f"{du.mean()/du.std(ddof=1):.2f}   (paper: 0.10, 0.66)")

print("\n" + "=" * 68)
print("TABLE 1 REPRODUCTION  (means per corpus)")
print("=" * 68)
print(f"  {'group':<12}{'cond':<10}{'SIM_E':>7}{'SIM_R':>7}{'SIM_W':>7}{'WER':>8}")
for gname, rs in (("All", rows), ("VCTK", vctk), ("Seed-TTS", seed)):
    for cond, pre in (("preset", "pre"), ("proposed", "opt")):
        print(f"  {gname:<12}{cond:<10}"
              f"{col(rs, f'{pre}_ecapa').mean():>7.3f}"
              f"{col(rs, f'{pre}_resnet').mean():>7.3f}"
              f"{col(rs, f'{pre}_wavlmsv').mean():>7.3f}"
              f"{col(rs, f'{pre}_wer').mean()*100:>7.2f}%")

print("\n" + "=" * 68)
print("PER-SPEAKER RANGE")
print("=" * 68)
oe = col(rows, "opt_ecapa")
print(f"  SIM_E range: {oe.min():.4f} - {oe.max():.4f}")
print(f"  SIM_W range: {col(rows, 'opt_wavlmsv').min():.4f} - "
      f"{col(rows, 'opt_wavlmsv').max():.4f}")
print(f"  speakers below preset baseline (SIM_E): "
      f"{int((oe < col(rows, 'pre_ecapa')).sum())}/154")
