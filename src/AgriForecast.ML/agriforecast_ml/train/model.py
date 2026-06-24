"""Pooled XGBoost harvest-price model: quantile intervals, purged walk-forward
CV, and a baseline-beat promotion gate.

Honest-data stance: with ~13 months of overlapping-window data this is a
baseline that must BEAT carry-forward to ship. If it does not, we do not
promote it — the API falls back to the naive predictor and we say so.
"""
from __future__ import annotations

import numpy as np
import pandas as pd
from xgboost import XGBRegressor

from . import baselines, dataset
from .evaluate import regression_metrics

QUANTILES = {"p10": 0.10, "p50": 0.50, "p90": 0.90}
SEED = 42


def make_model(alpha: float) -> XGBRegressor:
    return XGBRegressor(
        objective="reg:quantileerror", quantile_alpha=alpha,
        n_estimators=300, max_depth=4, learning_rate=0.05,
        subsample=0.8, colsample_bytree=0.8,
        reg_lambda=1.0, min_child_weight=5,
        tree_method="hist", enable_categorical=True, random_state=SEED,
    )


def _fit_predict_log(model, X_tr, y_tr, X_te):
    # train on log1p target (price is right-skewed); invert for output
    model.fit(X_tr, np.log1p(y_tr))
    return np.expm1(model.predict(X_te))


def purged_walk_forward(df: pd.DataFrame, X: pd.DataFrame, y: pd.Series, n_folds: int = 3):
    """Expanding-window CV with a purge: a train row is dropped if its harvest
    date falls on/after the test window start, so no training label peeks into
    the test period (prevents horizon leakage between folds)."""
    obs = df["ObservationDate"]
    label_date = obs + pd.to_timedelta(df["GrowthPeriodDays"].astype(int), unit="D")
    uniq = np.sort(obs.unique())
    test_region = uniq[int(len(uniq) * 0.6):]
    blocks = [b for b in np.array_split(test_region, n_folds) if len(b)]

    fold_rows = []
    for i, block in enumerate(blocks, 1):
        t_start = pd.Timestamp(block.min())
        test_mask = obs.isin(block)
        train_mask = (obs < t_start) & (label_date < t_start)
        if train_mask.sum() < 80 or test_mask.sum() < 20:
            continue
        model = make_model(0.5)
        pred = _fit_predict_log(model, X[train_mask], y[train_mask], X[test_mask])
        m = regression_metrics(y[test_mask], pred)
        cf = regression_metrics(y[test_mask], baselines.carry_forward_pred(df[test_mask]))
        cm = regression_metrics(y[test_mask], baselines.crop_mean_pred(df[train_mask], df[test_mask]))
        m.update(train=int(train_mask.sum()), test=int(test_mask.sum()),
                 carry_MAE=cf["MAE"], cropmean_MAE=cm["MAE"])
        fold_rows.append(m)
    return fold_rows


def _crop_fallback(df: pd.DataFrame) -> dict:
    """Per-crop harvest-price quantiles + global fallback — the deployable
    baseline used when no ML model is good enough to promote."""
    q = df.groupby("CropId")["LabelHarvestPrice"].quantile([0.1, 0.5, 0.9]).unstack()
    per_crop = {str(cid).lower(): {"p10": float(r[0.1]), "p50": float(r[0.5]), "p90": float(r[0.9])}
                for cid, r in q.iterrows()}
    g = df["LabelHarvestPrice"].quantile([0.1, 0.5, 0.9])
    return {"per_crop": per_crop,
            "global": {"p10": float(g[0.1]), "p50": float(g[0.5]), "p90": float(g[0.9])}}


def train_and_register(verbose=True):
    from ..registry import registry

    df = dataset.load_training_frame()
    X, y, cols = dataset.build_xy(df)
    chash = dataset.contract_hash(cols)

    if verbose:
        print(f"Training rows: {len(df)}  crops: {df['CropId'].nunique()}  features: {len(cols)}")
        print(f"Feature-contract hash: {chash}")

    folds = purged_walk_forward(df, X, y)
    if not folds:
        raise RuntimeError("Not enough data for walk-forward validation.")

    model_mae = float(np.mean([f["MAE"] for f in folds]))
    carry_mae = float(np.mean([f["carry_MAE"] for f in folds]))
    cropmean_mae = float(np.mean([f["cropmean_MAE"] for f in folds]))
    model_mape = float(np.mean([f["MAPE"] for f in folds]))

    # Gate against the BEST naive baseline, not the weakest one.
    best_baseline_mae = min(carry_mae, cropmean_mae)
    best_baseline_name = "crop_mean" if cropmean_mae <= carry_mae else "carry_forward"
    beats_baseline = model_mae < best_baseline_mae

    if verbose:
        print("\n=== Walk-forward folds ===")
        for i, f in enumerate(folds, 1):
            print(f"  fold {i}: train={f['train']} test={f['test']} "
                  f"model_MAE={f['MAE']} carry_MAE={f['carry_MAE']} cropmean_MAE={f['cropmean_MAE']}")
        print(f"\nMean MODEL MAE     : {model_mae:.2f}  (MAPE {model_mape:.1f}%)")
        print(f"Mean CARRY MAE     : {carry_mae:.2f}")
        print(f"Mean CROP-MEAN MAE : {cropmean_mae:.2f}")
        print(f"Best baseline      : {best_baseline_name} ({best_baseline_mae:.2f})")
        verdict = "PROMOTE (model beats best baseline)" if beats_baseline \
                  else f"DO NOT PROMOTE (model worse than {best_baseline_name}) -> serve fallback"
        print(f"Gate: {verdict}")

    # Final quantile models on all labelled data (kept for when the model improves).
    final = {q: make_model(a) for q, a in QUANTILES.items()}
    for q, mdl in final.items():
        mdl.fit(X, np.log1p(y))

    fallback = _crop_fallback(df)

    metadata = {
        "model": "ModelA_harvest_price",
        "algo": "pooled XGBoost (quantile)",
        "feature_cols": cols,
        "feature_contract_hash": chash,
        "quantiles": QUANTILES,
        "log_target": True,
        "cv": {"model_MAE": round(model_mae, 2), "model_MAPE": round(model_mape, 2),
               "carry_MAE": round(carry_mae, 2), "cropmean_MAE": round(cropmean_mae, 2),
               "best_baseline": best_baseline_name, "folds": folds},
        "beats_baseline": beats_baseline,
        "active_predictor": "model" if beats_baseline else "crop_mean_fallback",
        "n_train_rows": int(len(df)),
        "n_crops": int(df["CropId"].nunique()),
    }
    payload = {"models": final, "feature_cols": cols, "categorical": dataset.CATEGORICAL_COLS,
               "log_target": True, "quantiles": QUANTILES,
               "fallback": fallback, "beats_baseline": beats_baseline}

    # Always promote SOMETHING so serving has an active version: the model if it
    # earns it, otherwise this same version but flagged to serve the fallback.
    version = registry.save_model(payload, metadata, promote=True)
    if verbose:
        active = "ML model" if beats_baseline else "crop-mean fallback"
        print(f"\nRegistered {version} (active predictor: {active}).")
    return version, metadata
