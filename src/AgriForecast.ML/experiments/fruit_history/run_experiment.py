"""Fruit-history GATED experiment (Step 3 of the owner-approved fruit plan).

EXPERIMENT ONLY. No production writes, no promoted.json move, no CropFeatureDaily
overwrite. Builds control (A) and challenger (B[, C]) feature frames in memory
from a modified load_prices() output, runs a v13-faithful purged walk-forward on
MATCHED fold origins (fold blocks fixed from arm A), and reports every metric.

Run:
  cd src/AgriForecast.ML && set -a && . ./.env && set +a \
      && ./.venv/bin/python experiments/fruit_history/run_experiment.py
"""
from __future__ import annotations

import json
import os
from pathlib import Path

import numpy as np
import pandas as pd

from agriforecast_ml import features, load
from agriforecast_ml.envfile import load_env_file
from agriforecast_ml.db import get_engine
from agriforecast_ml.train import baselines, dataset
from agriforecast_ml.train.evaluate import regression_metrics, directional_accuracy
from agriforecast_ml.train.model import (
    make_model, _fit_predict_log, history_gated_crops, _DEFAULT_MIN_HISTORY_OBS,
)

SEED = 42
np.random.seed(SEED)
SPLICE = pd.Timestamp("2025-05-05")
OUTDIR = Path(__file__).resolve().parent

# CropId(lower) -> (label, gp). Papaya has gp=NULL -> excluded from labels.
FRUITS = {
    "bc018387-a122-47b7-87be-1621bea29b51": ("Ambul", 90),
    "02dc3010-7139-4212-8d6d-32685db85657": ("Kolikuttu", 120),
    "83f194dd-4ce9-456a-9aac-bfd98e287a7f": ("Seeni", 135),
    "75486680-eefb-414c-8e2c-6ca1f641ad17": ("Papaya", None),
}
# CropId from load_prices() is a str but may be upper/lower — normalise on compare.
FRUIT_IDS_LOWER = set(FRUITS.keys())
ML_FRUIT_IDS = {k for k, (_, gp) in FRUITS.items() if gp is not None}


def _cid_lower(s: pd.Series) -> pd.Series:
    return s.astype(str).str.lower()


# ---------------------------------------------------------------------------
# 1. Load shared national signals ONCE + both price frames.
# ---------------------------------------------------------------------------
def load_shared():
    crops = load.load_crops()
    weather = load.load_weather()
    fx = load.load_fx()
    sentiment = load.load_news_sentiment()
    policy = load.load_policy_flags()
    festivals = load.load_festivals()
    macro = load.load_macro_series()
    price_obs = load.load_price_observations()
    market_slugs = load.feature_safe_market_slugs()
    return dict(crops=crops, weather=weather, fx=fx, sentiment=sentiment,
                policy=policy, festivals=festivals, macro=macro,
                price_obs=price_obs, market_slugs=market_slugs)


def load_harti_pettah_presplice() -> pd.DataFrame:
    """HARTI Pettah rows for the 4 fruits, ObservedDate < 2025-05-05, shaped like
    load_prices() output (CropId/CropCode/CropName/MinPrice/MaxPrice/PriceDate/
    AvgPrice), deduped to one value per (CropId, PriceDate)."""
    ids = "','".join(FRUIT_IDS_LOWER)
    sql = f"""
        SELECT po.CropId, c.CropCode, c.Name AS CropName,
               po.ObservedDate AS PriceDate, po.MinPrice, po.MaxPrice
        FROM PriceObservations po
        JOIN Crops c ON c.Id = po.CropId
        JOIN Markets m ON m.Id = po.MarketId
        WHERE LOWER(CONVERT(varchar(36), po.CropId)) IN ('{ids}')
          AND m.Name = 'Pettah (HARTI wholesale)'
          AND po.Source = 'HARTI'
          AND po.IsUnitConfirmed = 1
          AND po.MaxPrice > 0
          AND po.ObservedDate < '2025-05-05'
    """
    df = pd.read_sql(sql, get_engine())
    df["CropId"] = _cid_lower(df["CropId"])
    df["PriceDate"] = pd.to_datetime(df["PriceDate"])
    for c in ("MinPrice", "MaxPrice"):
        df[c] = df[c].astype(float)
    df = (df.groupby(["CropId", "CropCode", "CropName", "PriceDate"], as_index=False)
            [["MinPrice", "MaxPrice"]].mean())
    df["AvgPrice"] = (df["MinPrice"] + df["MaxPrice"]) / 2.0
    return df


def build_prices_ab():
    prices_A = load.load_prices()
    prices_A["CropId"] = _cid_lower(prices_A["CropId"])
    harti = load_harti_pettah_presplice()
    # Guard: no (crop,date) collision with the DEC rows kept in prices_A.
    key = ["CropId", "PriceDate"]
    a_fruit = prices_A[prices_A["CropId"].isin(FRUIT_IDS_LOWER)][key]
    overlap = harti.merge(a_fruit, on=key, how="inner")
    assert overlap.empty, f"unexpected splice overlap: {len(overlap)} rows"
    prices_B = pd.concat([prices_A, harti[prices_A.columns]], ignore_index=True)
    prices_B = prices_B.sort_values(["CropId", "PriceDate"]).reset_index(drop=True)
    return prices_A, prices_B, harti


def build_frame(prices, shared, splice_dummy=False):
    """features.build_all -> labelled rows only, sorted by ObservationDate.

    splice_dummy=True adds a per-row `SourceIsHarti` column (arm C): 1 for fruit
    rows whose ObservationDate < 2025-05-05 (HARTI-sourced), else 0. It enters the
    model automatically via dataset.feature_columns auto-include."""
    feats = features.build_all(
        prices, shared["crops"], shared["weather"], shared["fx"],
        sentiment=shared["sentiment"], policy=shared["policy"],
        festivals=shared["festivals"], macro=shared["macro"],
        price_obs=shared["price_obs"], market_slugs=shared["market_slugs"])
    feats["CropId"] = _cid_lower(feats["CropId"])
    if splice_dummy:
        is_fruit = feats["CropId"].isin(FRUIT_IDS_LOWER)
        feats["SourceIsHarti"] = (
            is_fruit & (feats["ObservationDate"] < SPLICE)).astype("int8")
    df = feats[feats["LabelAvailable"] == 1].copy()
    df = df.sort_values("ObservationDate").reset_index(drop=True)
    return df


# ---------------------------------------------------------------------------
# 2. Fold blocks (fixed from control) + v13-faithful per-row CV.
# ---------------------------------------------------------------------------
def compute_fold_blocks(df_ref: pd.DataFrame, n_folds: int = 3):
    obs = df_ref["ObservationDate"]
    uniq = np.sort(obs.unique())
    test_region = uniq[int(len(uniq) * 0.6):]
    blocks = [b for b in np.array_split(test_region, n_folds) if len(b)]
    return blocks


def seasonal_naive_pred(df_all: pd.DataFrame, test_df: pd.DataFrame) -> np.ndarray:
    """y(t-365): the crop's AvgPrice on/before (HarvestDate - 365d), via a per-crop
    backward as-of over the FULL observed AvgPrice series (ffill semantics). NaN
    where no such prior price exists. Leakage-safe: HarvestDate-365 = obs+gp-365 <
    obs for gp<365."""
    src = (df_all[["CropId", "ObservationDate", "AvgPrice"]]
           .dropna(subset=["AvgPrice"]).sort_values("ObservationDate"))
    out = np.full(len(test_df), np.nan)
    tgt = (test_df["HarvestDate"] - pd.Timedelta(days=365))
    for cid, idx in test_df.groupby("CropId").groups.items():
        s = src[src["CropId"] == cid]
        if s.empty:
            continue
        left = pd.DataFrame({"t": tgt.loc[idx].values, "_i": list(idx)}).sort_values("t")
        merged = pd.merge_asof(left, s.rename(columns={"ObservationDate": "t"}),
                               on="t", direction="backward")
        out[[test_df.index.get_loc(i) for i in merged["_i"]]] = merged["AvgPrice"].values
    return out


def run_cv(df: pd.DataFrame, blocks, feature_cols_override=None):
    """v13 hybrid CV on FIXED fold blocks. Returns (per_row_df, fold_meta)."""
    X, y, cols = dataset.build_xy(df)
    if feature_cols_override is not None:
        cols = feature_cols_override
    obs = df["ObservationDate"]
    label_date = obs + pd.to_timedelta(df["GrowthPeriodDays"].astype(int), unit="D")

    rows = []
    fold_meta = []
    for i, block in enumerate(blocks, 1):
        t_start = pd.Timestamp(block.min())
        test_mask = obs.isin(block)
        train_mask = (obs < t_start) & (label_date < t_start)
        if train_mask.sum() < 80 or test_mask.sum() < 20:
            fold_meta.append({"fold": i, "skipped": True,
                              "train": int(train_mask.sum()),
                              "test": int(test_mask.sum())})
            continue
        Xtr, ytr, dtr = X[train_mask], y[train_mask], df[train_mask]
        Xte, yte, dte = X[test_mask], y[test_mask], df[test_mask]

        # --- v13 hybrid: gated pooled model (unweighted) + recency-mean fallback.
        gated = history_gated_crops(dtr, _DEFAULT_MIN_HISTORY_OBS)
        g_tr = dtr["CropId"].isin(gated).to_numpy()
        use_model = dte["CropId"].isin(gated).to_numpy()
        rw_pred = baselines.recency_weighted_crop_mean_pred(dtr, dte)
        if g_tr.sum() >= 50 and use_model.any():
            model = make_model(0.5)
            model_pred = _fit_predict_log(model, Xtr[g_tr], ytr[g_tr], Xte)
        else:
            model_pred = rw_pred.copy()
            use_model = np.zeros(len(dte), dtype=bool)
        hybrid_pred = np.where(use_model, model_pred, rw_pred)

        # baselines
        cf_pred = baselines.carry_forward_pred(dte)
        sn_pred = seasonal_naive_pred(df, dte)

        yte_np = yte.to_numpy(dtype=float)
        ref = dte["AvgPrice"].to_numpy(dtype=float)
        block_max = pd.Timestamp(block.max())
        for j in range(len(dte)):
            rows.append({
                "fold": i, "CropId": dte["CropId"].iloc[j],
                "obs": dte["ObservationDate"].iloc[j],
                "harvest": dte["HarvestDate"].iloc[j],
                "y": yte_np[j], "ref": ref[j],
                "hybrid": hybrid_pred[j], "model": model_pred[j],
                "recmean": rw_pred[j], "carry": cf_pred[j], "seasnaive": sn_pred[j],
                "use_model": bool(use_model[j]),
                # does this test row's label window straddle the splice?
                "straddles_splice": bool(
                    (dte["ObservationDate"].iloc[j] < SPLICE) and
                    (dte["HarvestDate"].iloc[j] >= SPLICE)),
            })
        fold_meta.append({
            "fold": i, "skipped": False,
            "t_start": str(t_start.date()), "t_end": str(block_max.date()),
            "train": int(train_mask.sum()), "test": int(test_mask.sum()),
            "n_gated_crops": len(gated),
            "fruit_gated": sorted([FRUITS[c][0] for c in gated if c in FRUITS]),
        })
    return pd.DataFrame(rows), fold_meta


# ---------------------------------------------------------------------------
# 3. Metric helpers.
# ---------------------------------------------------------------------------
def mae(y, p):
    m = ~(np.isnan(y) | np.isnan(p))
    return float(np.mean(np.abs(np.asarray(y)[m] - np.asarray(p)[m]))) if m.any() else None


def pooled_mae(pr: pd.DataFrame, pred_col="hybrid", crop_filter=None):
    d = pr if crop_filter is None else pr[crop_filter(pr)]
    return round(mae(d["y"].to_numpy(), d[pred_col].to_numpy()), 2), len(d)


def category_frt_quantiles(df: pd.DataFrame, cc: pd.DataFrame):
    """Reproduce _crop_fallback's by_category['FRT'] p10/p50/p90 over labelled rows
    of FRT-category crops (LabelHarvestPrice quantiles)."""
    frt_ids = set(cc.loc[cc["category_code"] == "FRT", "crop_id"].str.lower())
    sub = df[df["CropId"].isin(frt_ids)]
    if sub.empty:
        return None
    q = sub["LabelHarvestPrice"].quantile([0.1, 0.5, 0.9])
    contributors = (sub.groupby("CropId").size().sort_values(ascending=False))
    return {"p10": round(float(q[0.1]), 2), "p50": round(float(q[0.5]), 2),
            "p90": round(float(q[0.9]), 2), "n_rows": int(len(sub)),
            "n_crops": int(sub["CropId"].nunique()),
            "top_contributors": {str(k): int(v) for k, v in contributors.head(8).items()}}


# ---------------------------------------------------------------------------
def main():
    load_env_file()
    print("=== Fruit-history GATED experiment ===")
    shared = load_shared()
    cc = load.load_crop_categories()
    prices_A, prices_B, harti = build_prices_ab()
    print(f"HARTI Pettah pre-splice rows prepended: {len(harti)} "
          f"({harti['CropId'].nunique()} fruits)")
    for cid, g in harti.groupby("CropId"):
        print(f"  {FRUITS[cid][0]:10s} {len(g):5d} rows  "
              f"{g['PriceDate'].min().date()} -> {g['PriceDate'].max().date()}  "
              f"AvgPrice median={g['AvgPrice'].median():.1f}")

    df_A = build_frame(prices_A, shared)
    df_B = build_frame(prices_B, shared)
    df_C = build_frame(prices_B, shared, splice_dummy=True)
    print(f"\nLabelled rows  A={len(df_A)}  B={len(df_B)}  C={len(df_C)}")

    blocks = compute_fold_blocks(df_A)  # FIXED from control, reused for B & C.
    print(f"Fold blocks (from A): "
          + ", ".join(f"[{pd.Timestamp(b.min()).date()}..{pd.Timestamp(b.max()).date()}]"
                      for b in blocks))

    prA, fmA = run_cv(df_A, blocks)
    prB, fmB = run_cv(df_B, blocks)
    # Arm C: same challenger frame + splice dummy. Reuse B's contract + the extra col.
    colsC = dataset.feature_columns(df_C)
    prC, fmC = run_cv(df_C, blocks, feature_cols_override=colsC)

    results = {"seed": SEED, "splice_date": str(SPLICE.date()),
               "min_history_obs": _DEFAULT_MIN_HISTORY_OBS,
               "labelled_rows": {"A": len(df_A), "B": len(df_B), "C": len(df_C)},
               "harti_prepended": {FRUITS[c][0]: int(n) for c, n in
                                   harti.groupby("CropId").size().items()},
               "fold_meta": {"A": fmA, "B": fmB, "C": fmC}}

    # ---- Metric 1: pooled MAE + non-fruit guard ----
    is_fruit = lambda d: d["CropId"].isin(FRUIT_IDS_LOWER)
    non_fruit = lambda d: ~d["CropId"].isin(FRUIT_IDS_LOWER)
    ml_fruit = lambda d: d["CropId"].isin(ML_FRUIT_IDS)
    pooled = {}
    for name, pr in (("A", prA), ("B", prB), ("C", prC)):
        pm, n = pooled_mae(pr)
        nf, nfn = pooled_mae(pr, crop_filter=non_fruit)
        fr, frn = pooled_mae(pr, crop_filter=ml_fruit)
        pooled[name] = {"pooled_MAE": pm, "n": n,
                        "nonfruit_MAE": nf, "nonfruit_n": nfn,
                        "fruit_MAE": fr, "fruit_n": frn}
    results["pooled"] = pooled

    # ---- Metric 2/3: per-fruit-crop, per predictor + baselines, + folds ----
    per_crop = {}
    for cid in sorted(ML_FRUIT_IDS):
        label = FRUITS[cid][0]
        entry = {"gp": FRUITS[cid][1]}
        for arm_name, pr in (("A", prA), ("B", prB), ("C", prC)):
            d = pr[pr["CropId"] == cid]
            if d.empty:
                entry[arm_name] = {"n": 0}
                continue
            sn_cov = int(d["seasnaive"].notna().sum())
            entry[arm_name] = {
                "n": int(len(d)),
                "hybrid_MAE": round(mae(d["y"], d["hybrid"]), 2),
                "served_by": ("model" if d["use_model"].all() else
                              ("recency-mean" if not d["use_model"].any() else "mixed")),
                "recmean_MAE": round(mae(d["y"], d["recmean"]), 2),
                "carry_MAE": round(mae(d["y"], d["carry"]), 2),
                "seasnaive_MAE": (round(mae(d["y"], d["seasnaive"]), 2)
                                  if sn_cov else None),
                "seasnaive_coverage": f"{sn_cov}/{len(d)}",
                "straddle_rows": int(d["straddles_splice"].sum()),
                "per_fold_MAE": {int(f): round(mae(g["y"], g["hybrid"]), 2)
                                 for f, g in d.groupby("fold")},
            }
        per_crop[label] = entry
    results["per_fruit_crop"] = per_crop

    # ---- Metric 4: caveat quantification ----
    caveat = {}
    for arm_name, pr in (("A", prA), ("B", prB)):
        d = pr[ml_fruit(pr)]
        caveat[arm_name] = {
            "fruit_test_rows": int(len(d)),
            "fruit_test_origins": int(d["obs"].nunique()),
            "origins_straddling_splice": int(d.loc[d["straddles_splice"], "obs"].nunique()),
            "test_rows_straddling_splice": int(d["straddles_splice"].sum()),
        }
    # how many pre-splice fruit TRAIN rows does B add
    caveat["B_fruit_presplice_train_rows"] = int(
        (df_B[df_B["CropId"].isin(ML_FRUIT_IDS)]["ObservationDate"] < SPLICE).sum())
    results["caveat"] = caveat

    # ---- Metric 5: cold-start FRT category quantile shift ----
    results["cold_start_FRT"] = {
        "A": category_frt_quantiles(df_A, cc),
        "B": category_frt_quantiles(df_B, cc),
    }

    # ---- directional accuracy (fruit + pooled), reporting ----
    dir_acc = {}
    for arm_name, pr in (("A", prA), ("B", prB)):
        d = pr[ml_fruit(pr)]
        dir_acc[arm_name] = {
            "fruit_hybrid": directional_accuracy(d["y"], d["hybrid"], d["ref"])["directional_acc"],
            "pooled_hybrid": directional_accuracy(pr["y"], pr["hybrid"], pr["ref"])["directional_acc"],
        }
    results["directional_acc"] = dir_acc

    (OUTDIR / "results.json").write_text(json.dumps(results, indent=2, default=str))
    prA.to_csv(OUTDIR / "per_row_A.csv", index=False)
    prB.to_csv(OUTDIR / "per_row_B.csv", index=False)
    prC.to_csv(OUTDIR / "per_row_C.csv", index=False)
    print("\nWrote results.json + per_row_{A,B,C}.csv")
    print(json.dumps({k: results[k] for k in
                      ("pooled", "per_fruit_crop", "caveat", "cold_start_FRT",
                       "directional_acc")}, indent=2, default=str))


if __name__ == "__main__":
    main()
