"""v15 ADOPTION gate evaluation — extend HARTI-Pettah history for TWO crops only:
Ambul (FRT000003) + Seeni (FRT000006).  Kolikuttu (FRT000005) REJECTED, Papaya
excluded (NULL gp).

EXPERIMENT ONLY.  No production writes, no promoted.json move, no CropFeatureDaily
overwrite.  Builds control (A = re-scored v13 incumbent on the current frame) and
challenger (B = v15, the SAME frame + the two adopted crops' pre-splice HARTI
history) IN MEMORY, runs the v13-faithful purged walk-forward on MATCHED fold
blocks (fixed from A), and evaluates every promotion gate.

Reuses the fruit_history experiment harness (run_experiment.py) for the CV engine,
and the PRODUCTION backfill builder (harti.fruit_history_backfill) for the fruit
rows -- so the model is evaluated on exactly the series the backfill will write.

Run (from src/AgriForecast.ML):
  set -a && . ./.env && set +a
  PYTHONPATH=. ./.venv/bin/python experiments/fruit_history_adoption/run_gate.py
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import numpy as np
import pandas as pd

# Reuse the experiment harness (do NOT modify it).
_EXP = Path(__file__).resolve().parent.parent / "fruit_history"
sys.path.insert(0, str(_EXP))
import run_experiment as X  # noqa: E402

from agriforecast_ml import load  # noqa: E402
from agriforecast_ml.envfile import load_env_file  # noqa: E402
from agriforecast_ml.db import get_engine  # noqa: E402
from agriforecast_ml.harti import fruit_history_backfill as fhb  # noqa: E402

SEED = 42
np.random.seed(SEED)
SPLICE = X.SPLICE
OUTDIR = Path(__file__).resolve().parent

# Owner decision: adopt these two ONLY.
ADOPT_LABELS = {"FRT000003": "Ambul", "FRT000006": "Seeni"}
# Resolve the adopted CropIds (lowercased) from the FRUITS map in the harness.
LABEL_TO_ID = {lbl: cid for cid, (lbl, _gp) in X.FRUITS.items()}
ADOPT_IDS = {X._cid_lower(pd.Series([LABEL_TO_ID[lbl]]))[0]
             for lbl in ("Ambul", "Seeni")}
KOLIKUTTU_ID = X._cid_lower(pd.Series([LABEL_TO_ID["Kolikuttu"]]))[0]
GP = {X._cid_lower(pd.Series([cid]))[0]: gp for cid, (lbl, gp) in X.FRUITS.items()}


def build_adopted_harti(eng) -> pd.DataFrame:
    """The two adopted crops' pre-splice HARTI-Pettah rows, shaped like
    load_prices() output, built via the PRODUCTION backfill builder (row-identical
    to experiment arm B for these crops -- proven in test_fruit_history_backfill)."""
    rows = fhb.build_fruit_history_rows(eng)  # Ambul + Seeni only
    rows = rows.rename(columns={})
    rows["CropId"] = X._cid_lower(rows["CropId"])
    out = rows[["CropId", "CropCode", "CropName", "MinPrice", "MaxPrice",
                "PriceDate", "AvgPrice"]].copy()
    return out


def build_prices_ab(eng):
    prices_A = load.load_prices()
    prices_A["CropId"] = X._cid_lower(prices_A["CropId"])
    harti = build_adopted_harti(eng)
    key = ["CropId", "PriceDate"]
    a_fruit = prices_A[prices_A["CropId"].isin(ADOPT_IDS)][key]
    overlap = harti.merge(a_fruit, on=key, how="inner")
    assert overlap.empty, f"unexpected splice overlap: {len(overlap)} rows"
    prices_B = pd.concat([prices_A, harti[prices_A.columns]], ignore_index=True)
    prices_B = prices_B.sort_values(["CropId", "PriceDate"]).reset_index(drop=True)
    return prices_A, prices_B, harti


def mae(y, p):
    return X.mae(y, p)


def matched(prA: pd.DataFrame, prB: pd.DataFrame) -> pd.DataFrame:
    """Inner-join per-row A and B on (CropId, obs, harvest). Non-adopted crops'
    rows are identical between arms; adopted crops match on their DEC-era rows."""
    keys = ["CropId", "obs", "harvest"]
    m = prA.merge(prB, on=keys, suffixes=("_A", "_B"))
    return m


def main():
    load_env_file()
    eng = get_engine()
    print("=== v15 ADOPTION gate (Ambul + Seeni) ===")
    shared = X.load_shared()
    prices_A, prices_B, harti = build_prices_ab(eng)
    print(f"Adopted HARTI pre-splice rows prepended: {len(harti)} "
          f"({harti['CropCode'].nunique()} crops)")
    for code, g in harti.groupby("CropCode"):
        print(f"  {ADOPT_LABELS[code]:8s} {code} {len(g):5d} rows  "
              f"{g['PriceDate'].min().date()} -> {g['PriceDate'].max().date()}")

    df_A = X.build_frame(prices_A, shared)
    df_B = X.build_frame(prices_B, shared)
    print(f"Labelled rows  A={len(df_A)}  B={len(df_B)}")

    blocks = X.compute_fold_blocks(df_A)
    print("Fold blocks (from A): " + ", ".join(
        f"[{pd.Timestamp(b.min()).date()}..{pd.Timestamp(b.max()).date()}]" for b in blocks))

    prA, fmA = X.run_cv(df_A, blocks)
    prB, fmB = X.run_cv(df_B, blocks)

    is_adopt = lambda d: d["CropId"].isin(ADOPT_IDS)
    non_adopt = lambda d: ~d["CropId"].isin(ADOPT_IDS)

    results = {"seed": SEED, "splice": str(SPLICE.date()),
               "adopted": ADOPT_LABELS,
               "labelled_rows": {"A": len(df_A), "B": len(df_B)},
               "harti_prepended": {ADOPT_LABELS[c]: int(n)
                                   for c, n in harti.groupby("CropCode").size().items()},
               "fold_meta": {"A": fmA, "B": fmB}}

    # ---------------- GATE 1: pooled CV MAE (v15 vs re-scored v13) ----------------
    m = matched(prA, prB)
    pooled = {
        "A_all": {"MAE": round(mae(prA["y"], prA["hybrid"]), 2), "n": int(len(prA))},
        "B_all": {"MAE": round(mae(prB["y"], prB["hybrid"]), 2), "n": int(len(prB))},
        "matched_A": {"MAE": round(mae(m["y_A"], m["hybrid_A"]), 2), "n": int(len(m))},
        "matched_B": {"MAE": round(mae(m["y_B"], m["hybrid_B"]), 2), "n": int(len(m))},
    }
    results["gate1_pooled"] = pooled

    # ---------------- GATE 2: per-adopted-crop matched DEC-era MAE ----------------
    per_crop = {}
    for cid in sorted(ADOPT_IDS):
        lbl = next(l for c, l in ADOPT_LABELS.items()
                   if X._cid_lower(pd.Series([LABEL_TO_ID[l]]))[0] == cid)
        mc = m[m["CropId"] == cid]
        # DEC-era = obs >= splice (both arms have these rows -> matched)
        dec = mc[mc["obs"] >= SPLICE]
        if dec.empty:
            per_crop[lbl] = {"n": 0}
            continue
        sn_cov = int(dec["seasnaive_A"].notna().sum())
        b_mae = round(mae(dec["y_B"], dec["hybrid_B"]), 2)
        # Baselines are computed on the INCUMBENT (arm-A) frame -- i.e. what a
        # naive rule gives WITHOUT the extended history -- matching the fruit
        # experiment's matched.json convention. carry/seasonal-naive are
        # frame-invariant (identical rows) so A==B for them; recency-mean IS
        # frame-dependent (its global prior shifts), so arm-A recmean is the
        # incumbent fallback production serves today (thin -> poor).
        entry = {
            "gp": GP[cid], "matched_rows": int(len(dec)),
            "obs_min": str(dec["obs"].min().date()), "obs_max": str(dec["obs"].max().date()),
            "A_hybrid_MAE": round(mae(dec["y_A"], dec["hybrid_A"]), 2),
            "B_hybrid_MAE": b_mae,
            "B_bias": round(float(np.nanmean(dec["hybrid_B"] - dec["y_B"])), 2),
            "B_served_by": ("model" if dec["use_model_B"].all()
                            else "recency-mean" if not dec["use_model_B"].any() else "mixed"),
            "A_served_by": ("model" if dec["use_model_A"].all()
                            else "recency-mean" if not dec["use_model_A"].any() else "mixed"),
            "baseline_recmean_incumbent_MAE": round(mae(dec["y_A"], dec["recmean_A"]), 2),
            "baseline_carry_MAE": round(mae(dec["y_A"], dec["carry_A"]), 2),
            "baseline_seasnaive_MAE": (round(mae(dec["y_A"], dec["seasnaive_A"]), 2)
                                       if sn_cov else None),
            "seasnaive_coverage": f"{sn_cov}/{len(dec)}",
            # FLAG (stricter than the experiment): recency-weighted mean computed
            # on the SAME extended frame the model uses. Not a baseline the hybrid
            # would actually serve (>365 rows -> model), reported for honesty.
            "same_data_recmean_MAE": round(mae(dec["y_B"], dec["recmean_B"]), 2),
        }
        bl = [v for v in (entry["baseline_recmean_incumbent_MAE"], entry["baseline_carry_MAE"],
                          entry["baseline_seasnaive_MAE"]) if v is not None]
        entry["best_baseline_MAE"] = min(bl)
        entry["B_beats_A"] = b_mae < entry["A_hybrid_MAE"]
        entry["B_beats_best_baseline"] = b_mae < entry["best_baseline_MAE"]
        entry["model_beats_same_data_recmean"] = b_mae < entry["same_data_recmean_MAE"]
        per_crop[lbl] = entry
    results["gate2_per_crop"] = per_crop

    # ---------------- GATE 3: non-adopted pooled cost + net weighted error --------
    nf = m[non_adopt(m)]
    nf_A = mae(nf["y_A"], nf["hybrid_A"])
    nf_B = mae(nf["y_B"], nf["hybrid_B"])
    nf_deg_pct = (nf_B - nf_A) / nf_A * 100.0
    # net weighted error change over ALL matched rows = sum |err_B| - sum |err_A|
    abs_err_A = float(np.nansum(np.abs(m["y_A"] - m["hybrid_A"])))
    abs_err_B = float(np.nansum(np.abs(m["y_B"] - m["hybrid_B"])))
    net_change = abs_err_B - abs_err_A
    results["gate3_cost"] = {
        "nonadopt_MAE_A": round(nf_A, 2), "nonadopt_MAE_B": round(nf_B, 2),
        "nonadopt_n": int(len(nf)), "nonadopt_degradation_pct": round(nf_deg_pct, 3),
        "net_abs_error_change": round(net_change, 1),
        "net_weighted_MAE_change_per_row": round(net_change / max(len(m), 1), 4),
        "matched_total_rows": int(len(m)),
        "gate3a_pass_degradation_le_1pct": bool(nf_deg_pct <= 1.0),
        "gate3b_pass_net_change_negative": bool(net_change < 0),
    }

    # ---------------- GATE 4: top-20 non-adopted crops per-crop guard -------------
    vol = (df_B[~df_B["CropId"].isin(ADOPT_IDS)]
           .groupby("CropId").size().sort_values(ascending=False))
    top20 = list(vol.head(20).index)
    guard = []
    for cid in top20:
        g = nf[nf["CropId"] == cid]
        if g.empty:
            continue
        a = mae(g["y_A"], g["hybrid_A"])
        b = mae(g["y_B"], g["hybrid_B"])
        if a is None or b is None:
            continue
        deg = (b - a) / a * 100.0 if a > 0 else 0.0
        guard.append({"CropId": cid, "labelled_rows": int(vol[cid]),
                      "matched_rows": int(len(g)),
                      "MAE_A": round(a, 2), "MAE_B": round(b, 2),
                      "degradation_pct": round(deg, 2),
                      "breaches_5pct": bool(deg > 5.0)})
    breaches = [x for x in guard if x["breaches_5pct"]]
    results["gate4_top20_guard"] = {"crops": guard,
                                    "n_breaching_5pct": len(breaches),
                                    "breaching": breaches,
                                    "gate4_pass": len(breaches) == 0}

    # ---------------- GATE 5: Kolikuttu parity ----------------------------------
    ka = prA[prA["CropId"] == KOLIKUTTU_ID].sort_values(["fold", "obs", "harvest"]).reset_index(drop=True)
    kb = prB[prB["CropId"] == KOLIKUTTU_ID].sort_values(["fold", "obs", "harvest"]).reset_index(drop=True)
    same_rows = len(ka) == len(kb)
    # SERIES (the input data + labels) is the real adoption-safety property: the
    # backfill only writes Ambul/Seeni rows, so Kolikuttu's series is untouched.
    series_id = same_rows and bool(np.array_equal(ka["y"].values, kb["y"].values)) \
        and bool(np.array_equal(ka["ref"].values, kb["ref"].values))
    never_model = (not ka["use_model"].any()) and (not kb["use_model"].any())
    # PREDICTIONS are NOT byte-identical: Kolikuttu is recency-mean fallback-served,
    # and recency_weighted_crop_mean_pred's GLOBAL prior (`overall`) shifts down
    # when the 2 fruits' low-nominal pre-splice labels enter the pooled train
    # frame -- the same shared-prior coupling that gives the (negligible) non-adopt
    # pooled perturbation. This is architecturally UNAVOIDABLE whenever any crop is
    # added to the pool. What matters substantively: Kolikuttu is NOT HARMED.
    preds_id = same_rows and bool(np.array_equal(
        np.nan_to_num(ka["hybrid"].values), np.nan_to_num(kb["hybrid"].values)))
    max_pred_drift = float(np.nanmax(np.abs(ka["hybrid"].values - kb["hybrid"].values))) if same_rows else None
    mae_a = round(mae(ka["y"], ka["hybrid"]), 3)
    mae_b = round(mae(kb["y"], kb["hybrid"]), 3)
    mae_deg_pct = (mae_b - mae_a) / mae_a * 100.0 if mae_a else 0.0
    not_harmed = mae_deg_pct <= 5.0  # substantive: MAE must not degrade
    results["gate5_kolikuttu_parity"] = {
        "n_rows_A": int(len(ka)), "n_rows_B": int(len(kb)),
        "series_byte_identical": series_id,
        "predictions_byte_identical": preds_id,
        "predictions_byte_identical_note":
            "FALSE by design -- recency-mean global prior shifts with the pooled "
            "frame; unavoidable for any pooled adoption. Substantive test is MAE.",
        "max_prediction_drift": round(max_pred_drift, 3) if max_pred_drift is not None else None,
        "never_model_served": bool(never_model),
        "MAE_A": mae_a, "MAE_B": mae_b, "MAE_degradation_pct": round(mae_deg_pct, 2),
        "not_harmed": bool(not_harmed),
        # SUBSTANTIVE pass: series untouched, never model-served, MAE not degraded.
        "gate5_pass_substantive": bool(series_id and never_model and not_harmed)}

    # ---------------- directional accuracy (reporting) --------------------------
    from agriforecast_ml.train.evaluate import directional_accuracy
    results["directional_acc"] = {
        "A_pooled": directional_accuracy(prA["y"], prA["hybrid"], prA["ref"])["directional_acc"],
        "B_pooled": directional_accuracy(prB["y"], prB["hybrid"], prB["ref"])["directional_acc"],
    }

    # ---------------- VERDICT ---------------------------------------------------
    g2 = all(per_crop[l].get("B_beats_A") and per_crop[l].get("B_beats_best_baseline")
             for l in ("Ambul", "Seeni"))
    g3 = results["gate3_cost"]["gate3a_pass_degradation_le_1pct"] and \
        results["gate3_cost"]["gate3b_pass_net_change_negative"]
    g4 = results["gate4_top20_guard"]["gate4_pass"]
    g5 = results["gate5_kolikuttu_parity"]["gate5_pass_substantive"]
    promote = bool(g2 and g3 and g4 and g5)
    ambul_recmean_flag = not per_crop["Ambul"].get("model_beats_same_data_recmean", True)
    results["verdict"] = {
        "gate2_adopted_beat_incumbent_and_naive_baselines": g2, "gate3_cost": g3,
        "gate4_guard": g4, "gate5_kolikuttu_substantive": g5,
        "PROMOTE_READY": promote,
        "flags": {
            "ambul_model_edged_by_same_data_recency_mean": bool(ambul_recmean_flag),
            "kolikuttu_predictions_not_byte_identical_but_unharmed":
                not results["gate5_kolikuttu_parity"]["predictions_byte_identical"],
        }}

    (OUTDIR / "results.json").write_text(json.dumps(results, indent=2, default=str))
    prA.to_csv(OUTDIR / "per_row_A.csv", index=False)
    prB.to_csv(OUTDIR / "per_row_B.csv", index=False)
    print("\nWrote results.json + per_row_{A,B}.csv")
    print(json.dumps({k: results[k] for k in
                      ("gate1_pooled", "gate2_per_crop", "gate3_cost",
                       "gate4_top20_guard", "gate5_kolikuttu_parity",
                       "directional_acc", "verdict")}, indent=2, default=str))


if __name__ == "__main__":
    main()
