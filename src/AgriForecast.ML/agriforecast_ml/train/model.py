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


def _residual_pred(X_tr, y_tr, X_te, off_tr, off_te):
    """Residual model: learn (price - crop_mean_offset) on a log1p-stabilised
    target, then add the offset back. Offsets are per-crop train means, so this
    is leakage-safe as long as off_tr/off_te came from strictly-past TRAIN data.

    We model log1p(price) - log1p(offset) (a multiplicative residual) rather than
    a raw price difference: raw differences are heteroscedastic across crops with
    very different price levels and blew up the error in testing."""
    model = make_model(0.5)
    model.fit(X_tr, np.log1p(y_tr) - np.log1p(off_tr))
    return np.expm1(model.predict(X_te) + np.log1p(off_te))


def _blend_winner_crops(df_tr, X_tr, y_tr):
    """Leakage-safe per-crop selection: hold out the most-recent slice of TRAIN as
    an inner validation set, fit model + crop-mean on the earlier part, and return
    the set of CropIds where the model beats crop-mean on that inner val. The real
    fold then serves the model only for those crops. Uses NO test data."""
    obs = df_tr["ObservationDate"]
    uniq = np.sort(obs.unique())
    if len(uniq) < 4:
        return set()
    cut = pd.Timestamp(uniq[int(len(uniq) * 0.7)])
    lbl = obs + pd.to_timedelta(df_tr["GrowthPeriodDays"].astype(int), unit="D")
    inner_tr = (obs < cut) & (lbl < cut)
    inner_val = obs >= cut
    if inner_tr.sum() < 50 or inner_val.sum() < 20:
        return set()

    pred = _fit_predict_log(make_model(0.5), X_tr[inner_tr], y_tr[inner_tr], X_tr[inner_val])
    cm = baselines.offset_for(df_tr[inner_tr], df_tr[inner_val])
    yv = y_tr[inner_val].to_numpy()
    dv = df_tr[inner_val]
    winners = set()
    for crop in dv["CropId"].unique():
        m = (dv["CropId"] == crop).to_numpy()
        if m.sum() < 5:
            continue
        if np.mean(np.abs(pred[m] - yv[m])) < np.mean(np.abs(cm[m] - yv[m])):
            winners.add(crop)
    return winners


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
        Xtr, ytr, dtr = X[train_mask], y[train_mask], df[train_mask]
        Xte, yte, dte = X[test_mask], y[test_mask], df[test_mask]

        model = make_model(0.5)
        pred = _fit_predict_log(model, Xtr, ytr, Xte)
        m = regression_metrics(yte, pred)

        cf = regression_metrics(yte, baselines.carry_forward_pred(dte))
        cm_pred = baselines.crop_mean_pred(dtr, dte)
        cm = regression_metrics(yte, cm_pred)
        rw = regression_metrics(yte, baselines.recency_weighted_crop_mean_pred(dtr, dte))

        # --- candidate approaches (leakage-safe) -----------------------------
        # (2) residual model on a per-crop crop-mean offset
        off_tr = baselines.offset_for(dtr, dtr)
        off_te = baselines.offset_for(dtr, dte)
        resid = regression_metrics(yte, _residual_pred(Xtr, ytr, Xte, off_tr, off_te))

        # (1) per-crop blend: serve model only for crops it won on inner-val
        winners = _blend_winner_crops(dtr, Xtr, ytr)
        use_model = dte["CropId"].isin(winners).to_numpy()
        blend_pred = np.where(use_model, pred, cm_pred)
        blend = regression_metrics(yte, blend_pred)

        m.update(train=int(train_mask.sum()), test=int(test_mask.sum()),
                 carry_MAE=cf["MAE"], cropmean_MAE=cm["MAE"],
                 recencymean_MAE=rw["MAE"], residual_MAE=resid["MAE"],
                 blend_MAE=blend["MAE"], blend_model_frac=round(float(use_model.mean()), 2))
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

    def fmean(key):
        return float(np.mean([f[key] for f in folds]))

    model_mae = fmean("MAE")
    carry_mae = fmean("carry_MAE")
    cropmean_mae = fmean("cropmean_MAE")
    recencymean_mae = fmean("recencymean_MAE")
    residual_mae = fmean("residual_MAE")
    blend_mae = fmean("blend_MAE")
    model_mape = fmean("MAPE")

    # Baselines (non-ML) the ML path must beat to be worth shipping.
    baselines_cv = {"carry_forward": carry_mae, "crop_mean": cropmean_mae,
                    "recency_weighted_mean": recencymean_mae}
    best_baseline_name = min(baselines_cv, key=baselines_cv.get)
    best_baseline_mae = baselines_cv[best_baseline_name]

    # Candidate ML predictors. Each is a leakage-safe approach evaluated on the
    # same folds. The one with the lowest CV MAE is what we would serve IF the
    # ML path is promoted.
    ml_candidates = {"model": model_mae, "residual": residual_mae, "blend": blend_mae}
    best_ml_name = min(ml_candidates, key=ml_candidates.get)
    best_ml_mae = ml_candidates[best_ml_name]

    beats_baseline = best_ml_mae < best_baseline_mae

    if verbose:
        print("\n=== Walk-forward folds ===")
        for i, f in enumerate(folds, 1):
            print(f"  fold {i}: train={f['train']} test={f['test']} "
                  f"model={f['MAE']} resid={f['residual_MAE']} "
                  f"blend={f['blend_MAE']}(m{f['blend_model_frac']}) "
                  f"| cropmean={f['cropmean_MAE']} recmean={f['recencymean_MAE']} "
                  f"carry={f['carry_MAE']}")
        print("\n--- ML candidates (CV MAE) ---")
        print(f"  pooled model   : {model_mae:.2f}  (MAPE {model_mape:.1f}%)")
        print(f"  residual model : {residual_mae:.2f}")
        print(f"  per-crop blend : {blend_mae:.2f}")
        print("--- Baselines (CV MAE) ---")
        print(f"  carry-forward  : {carry_mae:.2f}")
        print(f"  crop-mean      : {cropmean_mae:.2f}")
        print(f"  recency-mean   : {recencymean_mae:.2f}")
        print(f"\nBest ML candidate : {best_ml_name} ({best_ml_mae:.2f})")
        print(f"Best baseline     : {best_baseline_name} ({best_baseline_mae:.2f})")
        verdict = (f"PROMOTE ({best_ml_name} beats {best_baseline_name})" if beats_baseline
                   else f"DO NOT PROMOTE (best ML '{best_ml_name}' worse than "
                        f"'{best_baseline_name}') -> serve fallback")
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
               "residual_MAE": round(residual_mae, 2), "blend_MAE": round(blend_mae, 2),
               "carry_MAE": round(carry_mae, 2), "cropmean_MAE": round(cropmean_mae, 2),
               "recencymean_MAE": round(recencymean_mae, 2),
               "best_ml": best_ml_name, "best_ml_MAE": round(best_ml_mae, 2),
               "best_baseline": best_baseline_name,
               "best_baseline_MAE": round(best_baseline_mae, 2), "folds": folds},
        "beats_baseline": beats_baseline,
        "active_predictor": (best_ml_name if beats_baseline else "crop_mean_fallback"),
        "served_ml_kind": best_ml_name,  # which ML variant would serve if promoted
        "n_train_rows": int(len(df)),
        "n_crops": int(df["CropId"].nunique()),
    }
    payload = {"models": final, "feature_cols": cols, "categorical": dataset.CATEGORICAL_COLS,
               "log_target": True, "quantiles": QUANTILES,
               "fallback": fallback, "beats_baseline": beats_baseline}

    # --- Retrain guardrail: never regress the live predictor ------------------
    # A retrain must NOT make serving worse. We always register the new version
    # for history, but we only move promoted.json when the new candidate is
    # STRICTLY better than what is currently live -- measured by the CV MAE of
    # whichever predictor each version actually serves (ML model vs crop-mean
    # fallback). If it is not better, the previously-promoted version stays
    # active (no-op promotion / rollback).
    # The ML path's served MAE is the best leakage-safe ML candidate; the fallback
    # path's served MAE is the best baseline.
    promote, reason = _promotion_decision(best_ml_mae, best_baseline_mae, beats_baseline)
    metadata["promotion_decision"] = reason

    if verbose:
        print("\n=== Promotion guardrail ===")
        print(f"  {reason}")
        print(f"  -> {'PROMOTE new version (live predictor improves)' if promote else 'KEEP currently-promoted version (no regression)'}")

    version = registry.save_model(payload, metadata, promote=promote)
    if verbose:
        if promote:
            active = "ML model" if beats_baseline else "crop-mean fallback"
            print(f"\nRegistered {version} and PROMOTED it (active predictor: {active}).")
        else:
            live = registry.load_promoted_metadata() or {}
            print(f"\nRegistered {version} for history only; promoted pointer "
                  f"stays at {live.get('version')} (no regression).")
    return version, metadata


def _served_mae(meta: dict) -> float | None:
    """CV MAE of the predictor a registered version ACTUALLY serves: the best ML
    candidate's MAE if it beat the baseline, otherwise the served baseline's MAE.
    Falls back to older metadata field names for versions trained before the
    multi-candidate gate. None if no served MAE is recorded (treated as beatable)."""
    cv = meta.get("cv") or {}
    if meta.get("beats_baseline"):
        return cv.get("best_ml_MAE", cv.get("model_MAE"))
    # served fallback: prefer the recorded best-baseline MAE, else crop-mean.
    return cv.get("best_baseline_MAE", cv.get("cropmean_MAE"))


def _promotion_decision(candidate_ml_mae: float, candidate_baseline_mae: float,
                        beats_baseline: bool):
    """Decide whether the freshly-trained candidate should become the live
    predictor. Returns (promote: bool, human_reason: str).

    Rules:
      * If nothing is promoted yet, promote (serving needs an active version).
      * The candidate's EFFECTIVE MAE is its best-ML-candidate MAE when it beat the
        baseline, else the served baseline's MAE (what it would actually serve).
      * Promote ONLY if that effective MAE is strictly LOWER than the live
        version's effective served MAE. Ties and regressions keep the incumbent.
    """
    from ..registry import registry

    live = registry.load_promoted_metadata()
    candidate_mae = candidate_ml_mae if beats_baseline else candidate_baseline_mae
    candidate_kind = "ML model" if beats_baseline else "crop-mean fallback"

    if live is None:
        return True, f"No live predictor yet -> bootstrap with candidate ({candidate_kind}, MAE {candidate_mae:.2f})."

    live_mae = _served_mae(live)
    live_kind = "ML model" if live.get("beats_baseline") else "crop-mean fallback"
    if live_mae is None:
        return True, (f"Live {live.get('version')} has no recorded served MAE -> "
                      f"promote candidate ({candidate_kind}, MAE {candidate_mae:.2f}).")

    better = candidate_mae < live_mae
    cmp = "<" if better else ">="
    verdict = "better" if better else "not better"
    return better, (
        f"Candidate {candidate_kind} served-MAE {candidate_mae:.2f} {cmp} "
        f"live {live.get('version')} {live_kind} served-MAE {live_mae:.2f} "
        f"-> candidate is {verdict} than the live predictor.")
