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
from .evaluate import directional_accuracy, regression_metrics

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


def _fit_predict_log(model, X_tr, y_tr, X_te, sample_weight=None):
    # train on log1p target (price is right-skewed); invert for output
    model.fit(X_tr, np.log1p(y_tr), sample_weight=sample_weight)
    return np.expm1(model.predict(X_te))


# Candidate recency half-lives (days) for the pooled gated fit, plus the unweighted
# control (None). Tuned on inner splits of each fold's TRAIN window only, so the test
# block is never touched. Choosing the unweighted control is a valid, honest outcome.
_HALFLIFE_GRID: list[float | None] = [90.0, 180.0, 365.0, 730.0, None]


def recency_weights(observation_dates: pd.Series, halflife_days: float | None,
                    t_ref: "pd.Timestamp | None" = None) -> np.ndarray:
    """Per-row exponential recency weight w = 0.5 ** (age_days / halflife).

    age_days is measured from t_ref (default: the max ObservationDate in observation_dates),
    so the most recent training row has weight 1.0 and older rows decay. t_ref must come
    from the CURRENT train window only, never a global or future max, or this leaks.

    halflife_days None, or non-positive, gives all-ones weights - the unweighted control.
    """
    n = len(observation_dates)
    if halflife_days is None or halflife_days <= 0:
        return np.ones(n, dtype=float)
    ref = observation_dates.max() if t_ref is None else t_ref
    age = (ref - observation_dates).dt.days.to_numpy(dtype=float)
    return 0.5 ** (age / float(halflife_days))


def _tune_halflife(df_tr: pd.DataFrame, X_tr: pd.DataFrame, y_tr: pd.Series,
                   grid: list | None = None):
    """Pick a recency half-life on inner splits of the fold's TRAIN window.

    Holds out the most recent ~15% of the train window as inner validation, purged so an
    inner-train row is dropped when its harvest label falls on or after the inner cutoff.
    Fits at each grid half-life with weights from the inner-train window only and keeps the
    lowest inner-val MAE. Never touches the fold's test block.

    Returns (best_halflife, inner_scores). A degenerate inner split returns (None, {}) and
    the caller falls back to the unweighted control.
    """
    grid = grid if grid is not None else _HALFLIFE_GRID
    obs = df_tr["ObservationDate"]
    uniq = np.sort(obs.unique())
    if len(uniq) < 5:
        return None, {}
    cut = pd.Timestamp(uniq[int(len(uniq) * 0.85)])
    lbl = obs + pd.to_timedelta(df_tr["GrowthPeriodDays"].astype(int), unit="D")
    inner_tr = ((obs < cut) & (lbl < cut)).to_numpy()   # purged inner-train
    inner_val = (obs >= cut).to_numpy()
    if inner_tr.sum() < 50 or inner_val.sum() < 20:
        return None, {}

    Xi_tr, yi_tr = X_tr[inner_tr], y_tr[inner_tr]
    Xi_val, yi_val = X_tr[inner_val], y_tr[inner_val]
    obs_itr = obs[inner_tr]
    t_ref = obs_itr.max()   # inner-train max only — no leak from val block
    yv = yi_val.to_numpy(dtype=float)

    scores: dict = {}
    for hl in grid:
        w = recency_weights(obs_itr, hl, t_ref=t_ref)
        pred = _fit_predict_log(make_model(0.5), Xi_tr, yi_tr, Xi_val, sample_weight=w)
        scores[hl] = float(np.mean(np.abs(pred - yv)))
    # Lowest inner-val MAE, tie-broken toward the unweighted control and then the longer
    # half-life - both the conservative choice.
    best = min(scores, key=lambda k: (scores[k],
                                      0 if k is None else 1,
                                      -(k or 0)))
    return best, scores


def _residual_pred(X_tr, y_tr, X_te, off_tr, off_te):
    """Residual model: learn price minus the per-crop mean offset, then add the offset back.

    Leakage-safe as long as off_tr and off_te came from strictly past TRAIN data. Models
    log1p(price) - log1p(offset), a multiplicative residual: raw price differences are
    heteroscedastic across crops with very different price levels and blew up the error.
    """
    model = make_model(0.5)
    model.fit(X_tr, np.log1p(y_tr) - np.log1p(off_tr))
    return np.expm1(model.predict(X_te) + np.log1p(off_te))


# Minimum labelled rows before a crop's own quantiles count as adequate history, and the
# history GATE deciding which crops the pooled model is trained on and served for.
# 365 is about one calendar year of daily rows, so per-crop quantiles span a full
# Yala+Maha cycle, and it cleanly separates thin crops from the HARTI-backed ones. The
# shipped predictor is a hybrid: pooled model on gated crops, recency-mean on thin ones.
# Keep ONE definition here; the value is persisted into the payload so serving reads it
# without a code change.
_DEFAULT_MIN_HISTORY_OBS = 365


def history_gated_crops(df: pd.DataFrame,
                        min_history_obs: int = _DEFAULT_MIN_HISTORY_OBS) -> set:
    """The CropIds with at least min_history_obs labelled rows in df - the model-served set.

    Callers MUST pass a point-in-time frame: the fold's TRAIN slice during walk-forward CV,
    so a crop is gated in only if it already had enough rows at that fold's train cutoff,
    and the full labelled frame at final-fit time.

    A plain row count: no model, no target peeking, fully deterministic. The single
    definition of the threshold is _DEFAULT_MIN_HISTORY_OBS.
    """
    counts = df.groupby("CropId").size()
    return set(counts[counts >= int(min_history_obs)].index)


def _incumbent_hybrid_mae() -> float | None:
    """CV hybrid MAE recorded by the currently-promoted version, if any.

    A candidate must beat the incumbent hybrid, not just a naive baseline. Returns None when
    nothing is promoted yet or the incumbent predates the field, which makes the incumbent
    gate a no-op. Comparable only while the feature store is unchanged: if the frame widens
    (new crops/history, as at v17) the recorded incumbent MAE is from a different data frame
    and the comparison is invalid — the guardrail does not yet detect this (open chip
    task_c9cd4432: fingerprint the store and re-score the incumbent on frame change).
    """
    from ..registry import registry
    live = registry.load_promoted_metadata()
    if not live:
        return None
    cv = live.get("cv") or {}
    v = cv.get("hybrid_MAE")
    return float(v) if v is not None else None


def _incumbent_gate(hybrid_mae: float) -> tuple[bool, float | None]:
    """Return (beats_incumbent, incumbent_hybrid_mae).

    Brick-safe: with nothing promoted, or an older metadata shape, the gate is a no-op
    (True, None) so we can never reach a state where nothing can ever be promoted.
    """
    incumbent = _incumbent_hybrid_mae()
    return (incumbent is None or hybrid_mae < incumbent), incumbent


def _blend_winner_crops(df_tr, X_tr, y_tr):
    """Per-crop selection on an inner validation slice of TRAIN, using no test data.

    Fits the model and the crop-mean on the earlier part of TRAIN and returns the CropIds
    where the model wins on the held-out recent slice.

    Superseded as the shipped selector by the deterministic history_gated_crops gate, but
    still backs the reporting-only 'blend' candidate in the walk-forward diagnostics.
    """
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

        cf_pred = baselines.carry_forward_pred(dte)
        cf = regression_metrics(yte, cf_pred)
        cm_pred = baselines.crop_mean_pred(dtr, dte)
        cm = regression_metrics(yte, cm_pred)
        rw_pred = baselines.recency_weighted_crop_mean_pred(dtr, dte)
        rw = regression_metrics(yte, rw_pred)

        # The hybrid candidate is the shipped predictor. The history gate counts TRAIN rows
        # only, so a crop is model-served in this fold only if it already had enough labelled
        # rows at the train cutoff. The pooled model is fit on gated crops ONLY - its served
        # set equals its train set - and thin crops fall to the recency-mean baseline, which is
        # exactly what production serves, so the CV measures the real predictor.
        gated = history_gated_crops(dtr, _DEFAULT_MIN_HISTORY_OBS)
        g_tr = dtr["CropId"].isin(gated).to_numpy()
        use_model = dte["CropId"].isin(gated).to_numpy()
        # Tune the half-life on inner splits of the gated TRAIN window only, then fit with
        # weights computed over that window using its own max as t_ref.
        chosen_hl, inner_scores = None, {}
        exp_thin_weighted_mae = None
        if g_tr.sum() >= 50 and use_model.any():
            dtr_g, Xtr_g, ytr_g = dtr[g_tr], Xtr[g_tr], ytr[g_tr]
            chosen_hl, inner_scores = _tune_halflife(dtr_g, Xtr_g, ytr_g)
            w_g = recency_weights(dtr_g["ObservationDate"], chosen_hl)
            hybrid_model_pred = _fit_predict_log(
                make_model(0.5), Xtr_g, ytr_g, Xte, sample_weight=w_g)
            # Report-only: the pooled model fit on ALL train crops (thin included) and scored on
            # the thin test segment. Evidence for a future served-set widening; changes nothing.
            if (~use_model).any():
                w_all = recency_weights(dtr["ObservationDate"], chosen_hl)
                thin_pred_all = _fit_predict_log(
                    make_model(0.5), Xtr, ytr, Xte, sample_weight=w_all)
                yte_thin = yte.to_numpy(dtype=float)[~use_model]
                exp_thin_weighted_mae = float(
                    np.mean(np.abs(thin_pred_all[~use_model] - yte_thin)))
        else:
            # Degenerate fold (no gated crops) -> the hybrid is all-baseline. Keep the array shape.
            hybrid_model_pred = rw_pred.copy()
            use_model = np.zeros(len(dte), dtype=bool)
        hybrid_pred = np.where(use_model, hybrid_model_pred, rw_pred)
        hybrid = regression_metrics(yte, hybrid_pred)
        yte_np = yte.to_numpy(dtype=float)
        # Per-segment MAE (served=gated / fallback=thin) for the fold corridor.
        seg_long = (regression_metrics(yte_np[use_model], hybrid_model_pred[use_model])["MAE"]
                    if use_model.any() else None)
        seg_thin = (regression_metrics(yte_np[~use_model], rw_pred[~use_model])["MAE"]
                    if (~use_model).any() else None)
        # Recency-mean baseline on the SAME thin rows, for a like-for-like comparison.
        exp_thin_recmean_mae = (
            regression_metrics(yte_np[~use_model], rw_pred[~use_model])["MAE"]
            if (~use_model).any() else None)

        # Candidate approaches, all leakage-safe.
        # (2) residual model on a per-crop crop-mean offset
        off_tr = baselines.offset_for(dtr, dtr)
        off_te = baselines.offset_for(dtr, dte)
        resid_pred = _residual_pred(Xtr, ytr, Xte, off_tr, off_te)
        resid = regression_metrics(yte, resid_pred)

        # (1) per-crop blend: serve model only for crops it won on inner-val
        winners = _blend_winner_crops(dtr, Xtr, ytr)
        use_model = dte["CropId"].isin(winners).to_numpy()
        blend_pred = np.where(use_model, pred, cm_pred)
        blend = regression_metrics(yte, blend_pred)

        # Directional accuracy (the go/no-go signal), reporting only.
        # Reference = the price known at the observation date, never a future one. Carry-
        # forward's own predicted move is therefore always flat, so its directional accuracy
        # is degenerate but recorded honestly for comparison.
        ref = dte["AvgPrice"].to_numpy(dtype=float)
        yte_arr = yte.to_numpy(dtype=float)
        m.update(
            model_dir_acc=directional_accuracy(yte_arr, pred, ref)["directional_acc"],
            residual_dir_acc=directional_accuracy(yte_arr, resid_pred, ref)["directional_acc"],
            blend_dir_acc=directional_accuracy(yte_arr, blend_pred, ref)["directional_acc"],
            hybrid_dir_acc=directional_accuracy(yte_arr, hybrid_pred, ref)["directional_acc"],
            carry_dir_acc=directional_accuracy(yte_arr, cf_pred, ref)["directional_acc"],
            cropmean_dir_acc=directional_accuracy(yte_arr, cm_pred, ref)["directional_acc"],
            recencymean_dir_acc=directional_accuracy(yte_arr, rw_pred, ref)["directional_acc"],
        )

        m.update(train=int(train_mask.sum()), test=int(test_mask.sum()),
                 carry_MAE=cf["MAE"], cropmean_MAE=cm["MAE"],
                 recencymean_MAE=rw["MAE"], residual_MAE=resid["MAE"],
                 blend_MAE=blend["MAE"], blend_model_frac=round(float(use_model.mean()), 2),
                 # v13 hybrid (the shipped predictor) + its per-segment MAE.
                 hybrid_MAE=hybrid["MAE"],
                 hybrid_model_frac=round(float(use_model.mean()), 3),
                 n_gated_crops=len(gated),
                 hybrid_long_seg_MAE=seg_long, hybrid_thin_seg_MAE=seg_thin,
                 # Half-life chosen this fold plus the inner-tuning scores. None means the unweighted
                 # control won, or the inner split was degenerate.
                 recency_halflife=chosen_hl,
                 recency_inner_scores={("inf" if k is None else k): round(v, 3)
                                       for k, v in inner_scores.items()},
                 # Report-only: weighted pooled model vs recency-mean on the thin test segment.
                 exp_thin_weighted_MAE=exp_thin_weighted_mae,
                 exp_thin_recmean_MAE=exp_thin_recmean_mae)
        fold_rows.append(m)
    return fold_rows


def _crop_fallback(df: pd.DataFrame) -> dict:
    """Per-crop harvest-price quantiles plus category and global fallbacks.

    The deployable baseline ladder used when no ML model is good enough to promote, and the
    prior for thin or unknown crops. The schema is additive, so older serving code degrades
    gracefully when a key is missing:
        per_crop[cid]    : {p10, p50, p90, n_obs}   (n_obs = labelled row count)
        by_category[cat] : {p10, p50, p90, n_obs}   (pooled category quantiles)
        global           : {p10, p50, p90}
        min_history_obs  : threshold for adequate own history
    """
    from ..serving.crop_categories import category_for

    counts = df.groupby("CropId").size()
    q = df.groupby("CropId")["LabelHarvestPrice"].quantile([0.1, 0.5, 0.9]).unstack()
    per_crop = {}
    names = df.groupby("CropId")["CropName"].first() if "CropName" in df.columns else {}
    for cid, r in q.iterrows():
        per_crop[str(cid).lower()] = {
            "p10": float(r[0.1]), "p50": float(r[0.5]), "p90": float(r[0.9]),
            "n_obs": int(counts.loc[cid]),
        }

    # Category-level quantiles: assign each labelled row a category, pool by it.
    cat_series = df["CropId"].map(
        lambda cid: category_for(str(cid).lower(),
                                  names.get(cid) if hasattr(names, "get") else None))
    by_category: dict[str, dict] = {}
    if cat_series.notna().any():
        tmp = df.assign(_cat=cat_series).dropna(subset=["_cat"])
        cq = tmp.groupby("_cat")["LabelHarvestPrice"].quantile([0.1, 0.5, 0.9]).unstack()
        ccnt = tmp.groupby("_cat").size()
        for cat, r in cq.iterrows():
            by_category[str(cat)] = {
                "p10": float(r[0.1]), "p50": float(r[0.5]), "p90": float(r[0.9]),
                "n_obs": int(ccnt.loc[cat]),
            }

    g = df["LabelHarvestPrice"].quantile([0.1, 0.5, 0.9])
    return {"per_crop": per_crop,
            "by_category": by_category,
            "min_history_obs": _DEFAULT_MIN_HISTORY_OBS,
            "global": {"p10": float(g[0.1]), "p50": float(g[0.5]), "p90": float(g[0.9])}}


def train_and_register(verbose=True, promote_override: bool | None = None):
    """Train, evaluate, and register Model A.

    `promote_override`:
        None  -> use the guardrail's decision (default).
        False -> register the new version for history but DO NOT move
                 promoted.json (used to stage a candidate for review before the
                 hub flips promotion).
        True  -> force-promote regardless of the guardrail (hub use only).
    """
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

    def fmean_dir(key):
        """Mean directional accuracy over folds, skipping folds where it was
        undefined (None -> no scored rows). None if no fold had a value."""
        vals = [f[key] for f in folds if f.get(key) is not None]
        return round(float(np.mean(vals)), 4) if vals else None

    model_mae = fmean("MAE")
    carry_mae = fmean("carry_MAE")
    cropmean_mae = fmean("cropmean_MAE")
    recencymean_mae = fmean("recencymean_MAE")
    residual_mae = fmean("residual_MAE")
    blend_mae = fmean("blend_MAE")
    hybrid_mae = fmean("hybrid_MAE")
    model_mape = fmean("MAPE")

    # Baselines (non-ML) the ML path must beat to be worth shipping.
    baselines_cv = {"carry_forward": carry_mae, "crop_mean": cropmean_mae,
                    "recency_weighted_mean": recencymean_mae}
    best_baseline_name = min(baselines_cv, key=baselines_cv.get)
    best_baseline_mae = baselines_cv[best_baseline_name]

    # Candidate ML predictors, each evaluated on the same folds. The SHIPPED predictor is the
    # hybrid (gated pooled model + recency-mean on thin crops), so that is what we promote
    # on. served_ml_kind stays 'model' and the fallback routing for non-gated crops happens
    # at serve time via served_on_crops. The others are reporting-only comparisons.
    ml_candidates = {"model": model_mae, "residual": residual_mae,
                     "blend": blend_mae, "hybrid": hybrid_mae}
    best_ml_name = "hybrid"  # hybrid is the served predictor
    best_ml_mae = hybrid_mae

    beats_baseline = best_ml_mae < best_baseline_mae

    # Incumbent gate: the candidate must also beat the promoted hybrid MAE, on the same folds
    # and rows. A no-op when nothing is promoted. It deliberately does NOT flip
    # beats_baseline, which must keep telling the true baseline story; losing to the
    # incumbent only blocks promotion, decided at the guardrail below.
    beats_incumbent, _incumbent_mae = _incumbent_gate(hybrid_mae)

    # Fold corridor: guard against a pass driven by fold averaging. The shipped hybrid must
    # also not regress against recency-mean in the volatile last fold, and the long-history
    # segment must not blow up. Within-fold checks only. A breach flips beats_baseline off,
    # so we never promote on fold noise.
    corridor_ok = True
    corridor_notes: list[str] = []
    if folds:
        last = folds[-1]
        f3_hybrid = last["hybrid_MAE"]
        f3_rw = last["recencymean_MAE"]
        if f3_hybrid > f3_rw:
            corridor_ok = False
            corridor_notes.append(
                f"final-fold hybrid MAE {f3_hybrid:.2f} exceeds recency-mean "
                f"{f3_rw:.2f} (volatile-regime regression).")
        # The long-history segment must not regress, with a small tolerance for fold jitter.
        long_segs = [f["hybrid_long_seg_MAE"] for f in folds
                     if f.get("hybrid_long_seg_MAE") is not None]
        _LONG_SEG_CEIL = 90.0  # v12 long-history CV segment was 77.43; ceiling w/ margin
        worst_long = max(long_segs) if long_segs else None
        if worst_long is not None and worst_long > _LONG_SEG_CEIL:
            corridor_ok = False
            corridor_notes.append(
                f"worst long-history segment MAE {worst_long:.2f} exceeds ceiling "
                f"{_LONG_SEG_CEIL:.2f}.")
    if not corridor_ok:
        beats_baseline = False

    if verbose:
        print("\n=== Walk-forward folds ===")
        for i, f in enumerate(folds, 1):
            _hl = f.get("recency_halflife")
            _hl_s = "inf" if _hl is None else f"{_hl:g}d"
            print(f"  fold {i}: train={f['train']} test={f['test']} "
                  f"HYBRID={f['hybrid_MAE']}(served {f['hybrid_model_frac']}, "
                  f"{f['n_gated_crops']} crops; long-seg={f['hybrid_long_seg_MAE']} "
                  f"thin-seg={f['hybrid_thin_seg_MAE']}; hl={_hl_s}) "
                  f"| pooled={f['MAE']} resid={f['residual_MAE']} "
                  f"blend={f['blend_MAE']} "
                  f"| cropmean={f['cropmean_MAE']} recmean={f['recencymean_MAE']} "
                  f"carry={f['carry_MAE']}")
            if f.get("recency_inner_scores"):
                print(f"          inner-tune MAE: " + ", ".join(
                    f"{k}={v}" for k, v in f["recency_inner_scores"].items()))
            if f.get("exp_thin_weighted_MAE") is not None:
                print(f"          [exploratory thin-seg] weighted-pooled="
                      f"{f['exp_thin_weighted_MAE']:.2f} vs recency-mean="
                      f"{f['exp_thin_recmean_MAE']:.2f}")
        print("\n--- ML candidates (CV MAE) ---")
        print(f"  HYBRID (shipped): {hybrid_mae:.2f}  <- history-gated pooled+recency")
        print(f"  pooled model    : {model_mae:.2f}  (MAPE {model_mape:.1f}%)")
        print(f"  residual model  : {residual_mae:.2f}")
        print(f"  per-crop blend  : {blend_mae:.2f}")
        print("--- Baselines (CV MAE) ---")
        print(f"  carry-forward   : {carry_mae:.2f}")
        print(f"  crop-mean       : {cropmean_mae:.2f}")
        print(f"  recency-mean    : {recencymean_mae:.2f}")
        print("--- Directional accuracy (CV mean, go/no-go; reporting only) ---")
        def _da(key):
            v = fmean_dir(key)
            return f"{v * 100:.1f}%" if v is not None else "n/a"
        print(f"  pooled model   : {_da('model_dir_acc')}   "
              f"crop-mean : {_da('cropmean_dir_acc')}   "
              f"recency : {_da('recencymean_dir_acc')}")
        print(f"\nServed ML candidate : {best_ml_name} ({best_ml_mae:.2f})")
        print(f"Best baseline       : {best_baseline_name} ({best_baseline_mae:.2f})")
        if _incumbent_mae is not None:
            _cmp = "<" if beats_incumbent else ">="
            print(f"Incumbent v13 hybrid: {_incumbent_mae:.2f} "
                  f"(v14 {hybrid_mae:.2f} {_cmp} incumbent -> "
                  f"{'beats' if beats_incumbent else 'DOES NOT beat'} incumbent)")
        else:
            print("Incumbent v13 hybrid: n/a (no recorded hybrid_MAE) -> "
                  "incumbent gate is a no-op")
        if corridor_notes:
            print("Corridor breaches   : " + "; ".join(corridor_notes))
        else:
            print("Corridor            : OK (final-fold not worse than recency-mean; "
                  "long-history segment within ceiling)")
        if beats_baseline and beats_incumbent:
            verdict = (f"PROMOTE ({best_ml_name} {best_ml_mae:.2f} beats "
                       f"{best_baseline_name} {best_baseline_mae:.2f} AND incumbent, "
                       f"corridor OK)")
        else:
            # Name the real failure: baseline gate, incumbent gate or corridor. Never say 'worse than
            # baseline' when the candidate beat the baseline but lost to the incumbent.
            beat_bl = best_ml_mae < best_baseline_mae
            if not beat_bl:
                why = f"'{best_ml_name}' {best_ml_mae:.2f} not below best baseline {best_baseline_mae:.2f}"
            elif not beats_incumbent:
                why = (f"'{best_ml_name}' {best_ml_mae:.2f} beats baseline "
                       f"{best_baseline_mae:.2f} BUT not incumbent v13 "
                       f"{_incumbent_mae:.2f}")
            else:
                why = "corridor breach"
            verdict = f"DO NOT PROMOTE ({why}) -> keep incumbent / serve fallback"
        print(f"Gate: {verdict}")
        # Festival-feature honesty: those columns were added on a domain prior plus leakage-
        # safety, NOT proven CV lift. With about 10 events per festival in the whole corpus (1-2
        # per fold) the per-festival lift is statistically unverifiable - do not read fold noise
        # as festival signal.
        print("\nNote: festival features are domain-prior + leakage-safe, NOT "
              "CV-proven (too few events until post-P3).")
        # Macro-feature honesty: the CBSL columns were added on leakage-safety (as-of on the
        # PublishedAt vintage, 60-day staleness cap, NaN not 0) plus a domain prior, not proven
        # lift. They are national series, identical across crops on a date, so they carry no
        # cross-sectional signal and the expected CV lift is about zero. Never promote on it.
        print("Note: macro features are national + leakage-safe, NOT CV-proven "
              "(no cross-sectional signal; expected lift ~0).")

    # Final history gate over the full labelled frame - the served set threaded into the
    # payload. Serving routes any crop not in it to the fallback ladder. Lowercased GUIDs so
    # they match serving's normalisation.
    gated_final = history_gated_crops(df, _DEFAULT_MIN_HISTORY_OBS)
    served_on_crops = sorted(str(c).lower() for c in gated_final)
    g_final_mask = df["CropId"].isin(gated_final).to_numpy()
    if verbose:
        print(f"\nHistory gate (final fit, >= {_DEFAULT_MIN_HISTORY_OBS} labelled "
              f"rows): {len(served_on_crops)} model-served crops / "
              f"{df['CropId'].nunique() - len(served_on_crops)} fallback crops.")

    # Final quantile models are fit on gated crops ONLY, so the model's served set equals its
    # train set and the pooled encoder's category set equals served_on_crops - a non-served
    # CropId can never reach the model.
    X_g, y_g = X[g_final_mask].copy(), y[g_final_mask]
    df_g = df[g_final_mask]
    # Drop unused CropId categories so the encoder's known set is exactly the gated crops.
    for c in dataset.CATEGORICAL_COLS:
        if c in X_g.columns and str(X_g[c].dtype) == "category":
            X_g[c] = X_g[c].cat.remove_unused_categories()
    # Tune the half-life on inner splits of the full gated training frame (leakage-safe:
    # there is no test block at final-fit time), then apply the SAME half-life to every
    # quantile head so p10/p50/p90 stay consistent.
    final_halflife, final_inner_scores = _tune_halflife(df_g, X_g, y_g)
    w_final = recency_weights(df_g["ObservationDate"], final_halflife)
    final = {q: make_model(a) for q, a in QUANTILES.items()}
    for q, mdl in final.items():
        mdl.fit(X_g, np.log1p(y_g), sample_weight=w_final)
    if verbose:
        _hl_str = "unweighted (inf)" if final_halflife is None else f"{final_halflife:g}d"
        print(f"\nv14 recency-weighting (final fit): chosen half-life = {_hl_str}")
        if final_inner_scores:
            print("  inner-tuning (final gated frame) grid MAE: " + ", ".join(
                f"{'inf' if k is None else f'{k:g}d'}={v:.2f}"
                for k, v in final_inner_scores.items()))

    # The residual models predict log1p(price) - log1p(offset) and serving adds the offset
    # back. The offset is a per-crop mean over all labelled TRAIN data, persisted below so
    # serving never recomputes it from future data. It is an additive log-space shift, so it
    # is identical across quantiles.
    cm_means, cm_overall = baselines.crop_mean_map(df)
    off_all = df["CropId"].map(cm_means).fillna(cm_overall).to_numpy(dtype=float)
    resid_target = np.log1p(y) - np.log1p(off_all)
    residual_models = {q: make_model(a) for q, a in QUANTILES.items()}
    for q, mdl in residual_models.items():
        mdl.fit(X, resid_target)
    # Persisted offset map: {lower(crop_id): offset_price}, + global fallback.
    residual_offsets = {str(cid).lower(): float(v) for cid, v in cm_means.items()}
    residual_offset_global = float(cm_overall)

    fallback = _crop_fallback(df)

    # Per-crop fallback-predictor selection. For every fallback-served crop, backtest the
    # recency-mean incumbent against the carry-forward challenger on a purged walk-forward.
    # A crop only switches when the challenger wins by at least 10% MAE over at least 30
    # origins and does not regress against the category-median tier serving deploys today.
    # The winners ride in the signed payload under fallback['choice'], and serving fails
    # closed to the incumbent for any crop absent from the map. Only carry-forward is shipped.
    from . import fallback_select
    fb_choice_map, fb_choice_table, fb_choice_agg = \
        fallback_select.select_fallback_choices(df, gated_final)
    fallback["choice"] = fb_choice_map
    if verbose:
        print(f"\n=== Fallback-predictor selection (fallback segment) ===")
        print(f"  switched {len(fb_choice_map)} crops to a non-recency-mean "
              f"fallback (all carry-forward).")
        print(f"  pooled fallback MAE (recency-mean incumbent) {fb_choice_agg['pooled_recmean_MAE']:.2f}"
              f" -> with switches {fb_choice_agg['pooled_switched_MAE']:.2f}")
        print(f"  vs REAL serving incumbent (category tier) "
              f"{fb_choice_agg['pooled_serving_category_MAE']:.2f} -> with switches "
              f"{fb_choice_agg['pooled_switched_vs_serving_MAE']:.2f}")
        print(f"  aggregate gate applied={fb_choice_agg['applied']} "
              f"({fb_choice_agg['reason']})")

    metadata = {
        "model": "ModelA_harvest_price",
        "algo": "pooled XGBoost (quantile)",
        "feature_cols": cols,
        "feature_contract_hash": chash,
        "quantiles": QUANTILES,
        "log_target": True,
        "cv": {"model_MAE": round(model_mae, 2), "model_MAPE": round(model_mape, 2),
               "residual_MAE": round(residual_mae, 2), "blend_MAE": round(blend_mae, 2),
               "hybrid_MAE": round(hybrid_mae, 2),
               "carry_MAE": round(carry_mae, 2), "cropmean_MAE": round(cropmean_mae, 2),
               "recencymean_MAE": round(recencymean_mae, 2),
               "best_ml": best_ml_name, "best_ml_MAE": round(best_ml_mae, 2),
               "best_baseline": best_baseline_name,
               "best_baseline_MAE": round(best_baseline_mae, 2),
               # v13 corridor verdict + per-fold segment MAEs (within-fold checks).
               "corridor_ok": corridor_ok, "corridor_notes": corridor_notes, "folds": folds,
               # Incumbent gate: the candidate must beat BOTH the naive baseline and the promoted
               # hybrid MAE, measured on the same folds.
               "incumbent_hybrid_MAE": _incumbent_mae,
               "beats_incumbent": beats_incumbent,
               # Directional accuracy is reporting only, not a gate input. Pooled = mean over folds.
               # Reference price = AvgPrice known at the observation date; carry-forward's move is
               # always flat, so its value is degenerate but recorded for honesty.
               "dir_acc": {
                   "model": fmean_dir("model_dir_acc"),
                   "residual": fmean_dir("residual_dir_acc"),
                   "blend": fmean_dir("blend_dir_acc"),
                   "carry_forward": fmean_dir("carry_dir_acc"),
                   "crop_mean": fmean_dir("cropmean_dir_acc"),
                   "recency_weighted_mean": fmean_dir("recencymean_dir_acc"),
               }},
        "beats_baseline": beats_baseline,
        # active_predictor describes the shipped predictor for humans: the hybrid when promoted,
        # otherwise the crop-mean fallback ladder.
        "active_predictor": ("hybrid" if beats_baseline else "crop_mean_fallback"),
        # served_ml_kind is the ARTIFACT kind serving uses for gated crops. Keep it 'model':
        # fallback routing for non-gated crops happens via served_on_crops, and serving only
        # honours {'model', 'residual'}.
        "served_ml_kind": "model",
        # The crops the ML model is served for; anything else routes to the fallback ladder at
        # serve time. Lowercased GUID strings.
        "served_on_crops": served_on_crops,
        "n_served_crops": len(served_on_crops),
        # The same map rides in the signed payload under fallback['choice']; this block is the
        # human-auditable record of the decision and its gate numbers.
        "fallback_choice": {
            "map": fb_choice_map,
            "n_switched": len(fb_choice_map),
            "aggregate": fb_choice_agg,
            "min_origins": fallback_select.DEFAULT_MIN_ORIGINS,
            "switch_margin": fallback_select.DEFAULT_SWITCH_MARGIN,
            "servable_challengers": list(fallback_select.DEFAULT_SERVABLE_CHALLENGERS),
            "table": fb_choice_table,
        },
        "min_history_obs": _DEFAULT_MIN_HISTORY_OBS,
        "n_train_rows": int(len(df)),
        "n_crops": int(df["CropId"].nunique()),
        # Recency weighting on the pooled gated fit. The half-life is tuned leakage-safe on inner
        # splits of the gated train window; None means the unweighted control. Recorded for audit:
        # the weights are a deterministic function of ObservationDate and this half-life.
        "recency_weighting": {
            "final_halflife_days": final_halflife,
            "grid": [("inf" if h is None else h) for h in _HALFLIFE_GRID],
            "final_inner_scores": {("inf" if k is None else k): round(v, 3)
                                   for k, v in final_inner_scores.items()},
            "per_fold_halflife": [("inf" if f.get("recency_halflife") is None
                                   else f.get("recency_halflife")) for f in folds],
        },
    }
    payload = {"models": final, "feature_cols": cols, "categorical": dataset.CATEGORICAL_COLS,
               "log_target": True, "quantiles": QUANTILES,
               "fallback": fallback, "beats_baseline": beats_baseline,
               # served_ml_kind tells serving which artifact path to use; residual artifacts are
               # persisted so serving can honour served_ml_kind='residual'.
               "served_ml_kind": "model",
               # The model-served crop set. Serving MUST route a crop not in this set to the fallback
               # ladder - an intentional decision, not an exception.
               "served_on_crops": served_on_crops,
               # Half-life used for the fit; audit only, since the models already bake in the weights.
               "recency_halflife_days": final_halflife,
               "residual_models": residual_models,
               "residual_offsets": residual_offsets,
               "residual_offset_global": residual_offset_global}

    # Retrain guardrail: never regress the live predictor. Every run registers a new version
    # for history, but promoted.json only moves when the candidate is strictly better than
    # what is live, measured by the CV MAE of whichever predictor each version actually
    # serves. Otherwise the previously promoted version stays active.
    #
    # A candidate that honestly beats its baselines but not the incumbent's hybrid MAE is
    # NOT promoted, with its own explicit reason, so the baseline-centric wording below is
    # never borrowed for an incumbent loss. beats_baseline stays true in the metadata.
    if beats_baseline and not beats_incumbent:
        promote = False
        reason = (f"Candidate hybrid MAE {hybrid_mae:.2f} beats its own baselines "
                  f"but not the incumbent's recorded hybrid MAE {_incumbent_mae:.2f} "
                  f"(same store/folds) -> incumbent stays promoted.")
    else:
        promote, reason = _promotion_decision(best_ml_mae, best_baseline_mae, beats_baseline,
                                              candidate_cropmean_mae=cropmean_mae)
    metadata["promotion_decision"] = reason
    metadata["promotion_recommended"] = promote  # the guardrail's verdict (for review)

    if promote_override is not None and promote_override != promote:
        reason = (f"{reason} [OVERRIDDEN: caller forced promote={promote_override}]")
    effective_promote = promote if promote_override is None else promote_override

    if verbose:
        print("\n=== Promotion guardrail ===")
        print(f"  {reason}")
        rec = 'PROMOTE new version (live predictor improves)' if promote else 'KEEP currently-promoted version (no regression)'
        print(f"  guardrail recommends -> {rec}")
        if promote_override is not None:
            print(f"  promote_override={promote_override} -> effective promote={effective_promote}")

    version = registry.save_model(payload, metadata, promote=effective_promote)
    promote = effective_promote
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
                        beats_baseline: bool, candidate_cropmean_mae: float | None = None):
    """Decide whether the freshly-trained candidate should become the live predictor.

    Returns (promote, human_reason).

    The decision is the within-fold gate, never a cross-version MAE comparison: a data-regime
    change (a corpus expansion, for instance) shifts the walk-forward test distribution, so
    absolute MAE from different versions is not comparable, and comparing them once wrongly
    blocked a model that beat every baseline on its own folds.

    Rules:
      * Nothing promoted yet -> promote; serving needs an active version.
      * The candidate's ML path beats its own baselines -> promote. It is provably better
        than every baseline on the current data, including the crop-mean the incumbent serves.
      * Otherwise it would serve the same crop-mean fallback as before, so only re-promote
        when the live version is also a non-beating fallback AND the candidate's own-fold
        crop-mean MAE is strictly lower. Otherwise keep the incumbent.
    """
    from ..registry import registry

    live = registry.load_promoted_metadata()

    if live is None:
        kind = "ML model" if beats_baseline else "crop-mean fallback"
        mae = candidate_ml_mae if beats_baseline else candidate_baseline_mae
        return True, f"No live predictor yet -> bootstrap with candidate ({kind}, MAE {mae:.2f})."

    if beats_baseline:
        return True, (
            f"Candidate ML model beats every baseline on its OWN walk-forward "
            f"folds (best-ML {candidate_ml_mae:.2f} < best-baseline "
            f"{candidate_baseline_mae:.2f}) -> PROMOTE. (Regime-aware: NOT "
            f"compared to live {live.get('version')}'s recorded MAE, which was "
            f"measured on a different data regime.)")

    # Candidate serves the crop-mean fallback (did not beat its baselines).
    if bool(live.get("beats_baseline")):
        return False, (
            f"Candidate did NOT beat its baselines (serves crop-mean fallback) "
            f"but live {live.get('version')} serves an ML model -> KEEP incumbent.")
    live_cm = (live.get("cv") or {}).get("cropmean_MAE")
    cand_cm = candidate_cropmean_mae
    if live_cm is None or cand_cm is None:
        return False, (
            f"Candidate and live both serve crop-mean fallback; no comparable "
            f"crop-mean MAE recorded -> KEEP incumbent (no regression).")
    better = cand_cm < live_cm
    cmp = "<" if better else ">="
    return better, (
        f"Both serve crop-mean fallback: candidate own-fold crop-mean "
        f"{cand_cm:.2f} {cmp} live {live.get('version')} {live_cm:.2f} -> "
        f"{'promote' if better else 'keep incumbent'}.")
