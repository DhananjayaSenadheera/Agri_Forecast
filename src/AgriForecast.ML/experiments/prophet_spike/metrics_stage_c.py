"""Stage C -- METRICS + VERDICT.  EVALUATION-ONLY.

Merges Stage-A origins with Stage-B Prophet forecasts, computes MAE/RMSE/MAPE +
directional accuracy per crop, per fold and overall, for Prophet / seasonal-naive
/ carry-forward / v13 on the IDENTICAL matched origin set at h=gp (and h=7/30 for
context, v13 excluded there). Applies the ship gate (Prophet beats BOTH
seasonal-naive AND v13 on BOTH crops at h=gp) and a splice-sensitivity cut.
Writes RESULTS.md + results.json + per_origin.csv into experiments/prophet_spike/.

Reuses agriforecast_ml.train.evaluate (same regression_metrics /
directional_accuracy the v13 trainer uses) so the numbers are apples-to-apples.
"""
from __future__ import annotations

import json
import os

import numpy as np
import pandas as pd

from agriforecast_ml.train.evaluate import regression_metrics, directional_accuracy

SANDBOX = ("/private/tmp/claude-501/-Users-dhananjayasenadheera-Projects-Agri-"
           "Forecast/36778f71-aa24-42a1-8085-3a7f69884e49/scratchpad/"
           "prophet-sandbox")
DATA = os.path.join(SANDBOX, "data")
OUT = os.path.dirname(os.path.abspath(__file__))

CROPS = [("Capsicum", "VEG000015", 75), ("Ridge Gourd", "VEG000057", 65)]
# competitor -> prediction column, per horizon key
COMPETITORS_GP = ["prophet", "snaive", "carry", "v13"]
COMPETITORS_CTX = ["prophet", "snaive", "carry"]


def _metrics(y_true, y_pred, ref):
    m = regression_metrics(y_true, y_pred)
    d = directional_accuracy(y_true, y_pred, ref)
    m["dir_acc"] = d["directional_acc"]
    m["n"] = int(len(y_true))
    return m


def compute_for_horizon(dfm: pd.DataFrame, hk: str, competitors: list) -> dict:
    """Matched-origin metrics at horizon hk. Drops rows where y_true or ANY
    competitor prediction is NaN. Returns per-fold + overall metric blocks and
    the drop accounting."""
    ycol, ref = f"y_true_{hk}", "y_origin"
    pred_cols = {c: (f"{c}_{hk}" if c != "carry" else f"carry_{hk}") for c in competitors}
    need = [ycol, ref] + list(pred_cols.values())
    present = dfm.dropna(subset=need)
    n_total, n_kept = len(dfm), len(present)
    res = {"horizon": hk, "n_total_origins": n_total, "n_matched": n_kept,
           "n_dropped": n_total - n_kept, "overall": {}, "by_fold": {}}
    # drop reasons
    reasons = {}
    for col in need:
        miss = int(dfm[col].isna().sum())
        if miss:
            reasons[col] = miss
    res["drop_reasons_any_nan"] = reasons

    def block(sub):
        yt = sub[ycol].to_numpy(float)
        rf = sub[ref].to_numpy(float)
        out = {}
        for c in competitors:
            out[c] = _metrics(yt, sub[pred_cols[c]].to_numpy(float), rf)
        return out

    res["overall"] = block(present)
    for f in sorted(present["fold"].unique()):
        res["by_fold"][int(f)] = block(present[present["fold"] == f])
    return res


def gate(prophet_mae, snaive_mae, v13_mae):
    beats_sn = prophet_mae < snaive_mae
    beats_v13 = prophet_mae < v13_mae
    return beats_sn, beats_v13, (beats_sn and beats_v13)


def fmt_row(label, m):
    da = "n/a" if m["dir_acc"] is None else f"{m['dir_acc']*100:.1f}%"
    return f"| {label} | {m['MAE']:.2f} | {m['RMSE']:.2f} | {m['MAPE']:.1f}% | {da} | {m['n']} |"


def main() -> None:
    results = {"crops": {}, "verdict": {}}
    md = []
    md.append("# Prophet gated R&D spike -- RESULTS (Steps 2-3)\n")
    md.append("EVALUATION-ONLY. No production/serving/registry change. ClickUp 86cahefgb.\n")
    md.append("Protocol: PLAN.md. Prophet = default Prophet + holidays-only, MAP (mcmc=0), "
              "seed 42, L-BFGS iter cap 10000, per-origin refit on `ds < origin`. "
              "v13 = per-origin point-in-time refit of the pooled history-gated hybrid "
              "(p50), predicted for the (crop, origin) row. Shared `y_true` = backward "
              "as-of of the daily series at `origin+h` (= v13's own label convention). "
              "Seasonal-naive = `y(origin+h - 365d)` (backward as-of, tol 30d). "
              "Carry-forward = `y(origin)`. Directional-accuracy reference = `y(origin)`.\n")

    merged_all = []
    export_sum = json.load(open(os.path.join(DATA, "export_summary.json")))
    prophet_sum = json.load(open(os.path.join(DATA, "prophet_summary.json")))

    for name, code, gp in CROPS:
        o = pd.read_csv(os.path.join(DATA, f"origins_{code}.csv"))
        p = pd.read_csv(os.path.join(DATA, f"prophet_{code}.csv"))
        dfm = o.merge(p, on="origin", how="left")
        dfm["crop_name"] = name
        merged_all.append(dfm)

        crop_res = {"gp": gp, "horizons": {}, "splice": {}}
        # h = gp (with v13), plus context h=7,30 (no v13)
        crop_res["horizons"]["gp"] = compute_for_horizon(dfm, "gp", COMPETITORS_GP)
        crop_res["horizons"]["7"] = compute_for_horizon(dfm, "7", COMPETITORS_CTX)
        crop_res["horizons"]["30"] = compute_for_horizon(dfm, "30", COMPETITORS_CTX)

        # splice sensitivity at h=gp: pre-splice targets only (target before splice)
        pre = dfm[dfm["target_post_splice_gp"] == 0]
        post = dfm[dfm["target_post_splice_gp"] == 1]
        crop_res["splice"]["gp_pre_splice"] = compute_for_horizon(pre, "gp", COMPETITORS_GP)
        crop_res["splice"]["gp_post_splice"] = (
            compute_for_horizon(post, "gp", COMPETITORS_GP) if len(post) else None)
        crop_res["splice"]["n_pre"] = int(len(pre))
        crop_res["splice"]["n_post"] = int(len(post))
        results["crops"][code] = crop_res

        # ---- markdown per crop ----
        md.append(f"\n## {name} ({code}) -- h=gp={gp}d\n")
        gpb = crop_res["horizons"]["gp"]
        md.append(f"Matched origins: {gpb['n_matched']}/{gpb['n_total_origins']} "
                  f"(dropped {gpb['n_dropped']}; reasons: {gpb['drop_reasons_any_nan'] or 'none'}).\n")
        md.append("\n### Overall (h=gp)\n")
        md.append("| model | MAE | RMSE | MAPE | dir-acc | n |")
        md.append("|---|---|---|---|---|---|")
        for c in COMPETITORS_GP:
            md.append(fmt_row(c, gpb["overall"][c]))
        # per fold
        md.append("\n### Per fold (h=gp), MAE [dir-acc]\n")
        md.append("| fold | prophet | snaive | carry | v13 | n |")
        md.append("|---|---|---|---|---|---|")
        for f, blk in gpb["by_fold"].items():
            def cell(c):
                m = blk[c]; da = "-" if m["dir_acc"] is None else f"{m['dir_acc']*100:.0f}%"
                return f"{m['MAE']:.1f} [{da}]"
            md.append(f"| {f} | {cell('prophet')} | {cell('snaive')} | {cell('carry')} "
                      f"| {cell('v13')} | {blk['prophet']['n']} |")

        # gate for this crop (overall)
        pm = gpb["overall"]["prophet"]["MAE"]
        sm = gpb["overall"]["snaive"]["MAE"]
        vm = gpb["overall"]["v13"]["MAE"]
        beats_sn, beats_v13, passes = gate(pm, sm, vm)
        results["verdict"][code] = {
            "prophet_MAE": pm, "snaive_MAE": sm, "v13_MAE": vm,
            "beats_snaive": beats_sn, "beats_v13": beats_v13, "passes": passes}
        md.append(f"\n**Gate ({name}):** Prophet {pm:.2f} vs seasonal-naive {sm:.2f} "
                  f"({'BEATS' if beats_sn else 'LOSES'}) & vs v13 {vm:.2f} "
                  f"({'BEATS' if beats_v13 else 'LOSES'}) -> "
                  f"{'PASS' if passes else 'FAIL'}\n")

        # context horizons
        for hk in ("7", "30"):
            hb = crop_res["horizons"][hk]
            md.append(f"\n### Context h={hk}d (no v13) -- matched {hb['n_matched']}/{hb['n_total_origins']}\n")
            md.append("| model | MAE | RMSE | MAPE | dir-acc | n |")
            md.append("|---|---|---|---|---|---|")
            for c in COMPETITORS_CTX:
                md.append(fmt_row(c, hb["overall"][c]))

        # splice
        md.append(f"\n### Splice sensitivity (h=gp) -- pre={crop_res['splice']['n_pre']} "
                  f"post={crop_res['splice']['n_post']} (splice 2025-05-05)\n")
        preb = crop_res["splice"]["gp_pre_splice"]
        md.append("Pre-splice-target origins only:\n")
        md.append("| model | MAE | RMSE | MAPE | dir-acc | n |")
        md.append("|---|---|---|---|---|---|")
        for c in COMPETITORS_GP:
            md.append(fmt_row(c, preb["overall"][c]))
        if crop_res["splice"]["gp_post_splice"]:
            postb = crop_res["splice"]["gp_post_splice"]
            md.append("\nPost-splice-target origins only:\n")
            md.append("| model | MAE | RMSE | MAPE | dir-acc | n |")
            md.append("|---|---|---|---|---|---|")
            for c in COMPETITORS_GP:
                md.append(fmt_row(c, postb["overall"][c]))

    # ---- overall verdict ----
    all_pass = all(v["passes"] for v in results["verdict"].values())
    results["ship_gate_pass"] = all_pass
    md.append("\n## VERDICT (ship gate)\n")
    md.append("Gate: Prophet ships only if it beats BOTH seasonal-naive AND v13 at h=gp on BOTH crops.\n")
    md.append("| crop | Prophet MAE | seasonal-naive MAE | v13 MAE | beats sn? | beats v13? | verdict |")
    md.append("|---|---|---|---|---|---|---|")
    for name, code, gp in CROPS:
        v = results["verdict"][code]
        md.append(f"| {name} | {v['prophet_MAE']:.2f} | {v['snaive_MAE']:.2f} | "
                  f"{v['v13_MAE']:.2f} | {'Y' if v['beats_snaive'] else 'N'} | "
                  f"{'Y' if v['beats_v13'] else 'N'} | {'PASS' if v['passes'] else 'FAIL'} |")
    md.append(f"\n**SHIP DECISION: {'SHIP' if all_pass else 'DO NOT SHIP'} "
              f"(Prophet {'passes' if all_pass else 'fails'} the gate).** "
              "Nothing is promoted or committed regardless -- this is an R&D spike.\n")

    # leakage + env evidence
    md.append("\n## Leakage self-check\n")
    md.append(f"- v13: per-origin train cut `ObservationDate < origin` AND `HarvestDate < origin` "
              f"(purge). Export asserted `max_train_obs < origin` for every origin; "
              f"`v13_leak_bad` = "
              f"{ {c['code']: c['v13_leak_bad'] for c in export_sum['crops']} }.\n")
    md.append(f"- Prophet: per-origin `train = series[ds < origin]`, in-code assert "
              f"`train.ds.max() < origin`; `leak_bad` = "
              f"{ {c['code']: c['leak_bad'] for c in prophet_sum['crops']} }.\n")
    md.append(f"- Prophet seasonalities activated (defaults): "
              f"{ {c['code']: c['seasonalities'] for c in prophet_sum['crops']} }.\n")
    md.append(f"- Wall time: Stage A export {export_sum['wall_time_s']}s, "
              f"Stage B Prophet {prophet_sum['wall_time_s']}s.\n")

    md.append("\n## Caveats\n")
    md.append("1. **v13 information asymmetry:** v13 uses lags/rollings/festival/macro/spread "
              "+ cross-crop pooling; Prophet uses univariate price + holidays only. Same target/"
              "origin, different feature sets -- a 'which predictor wins,' not 'same features.'\n")
    md.append("2. **Fold geometry:** origins = the crop's v13 feature ObservationDates in the last "
              "40%, split into 3 sequential blocks, weekly cadence. v13's own TRAINING rows differ "
              "(pooled, purged) even at matched origins.\n")
    md.append("3. **Series splice** (HARTI->Dambulla-DEC, 2025-05-05): both models cross it; v13 "
              "trained through it, Prophet sees it cold. See per-crop splice tables.\n")
    md.append("4. v13 published pooled CV MAE (100.31) is NOT the comparator; only the per-crop, "
              "same-origin v13 number above is a fair opponent.\n")

    with open(os.path.join(OUT, "RESULTS.md"), "w") as f:
        f.write("\n".join(md) + "\n")
    with open(os.path.join(OUT, "results.json"), "w") as f:
        json.dump(results, f, indent=2, default=str)
    full = pd.concat(merged_all, ignore_index=True)
    full.to_csv(os.path.join(OUT, "per_origin.csv"), index=False)

    print("=== Stage C done ===")
    print(f"ship_gate_pass = {all_pass}")
    for name, code, gp in CROPS:
        v = results["verdict"][code]
        print(f"  {name}: Prophet {v['prophet_MAE']:.2f} | sn {v['snaive_MAE']:.2f} "
              f"| v13 {v['v13_MAE']:.2f} -> {'PASS' if v['passes'] else 'FAIL'}")
    print(f"Wrote RESULTS.md, results.json, per_origin.csv to {OUT}")


if __name__ == "__main__":
    main()
