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


# --- v14: exponential recency sample-weighting ------------------------------
# Grid of candidate half-lives (days) for the pooled gated fit, plus the
# UNWEIGHTED control (halflife=None => all-ones weights). Tuned leakage-safe on
# inner splits of each fold's TRAIN window only; the fold's test block is NEVER
# touched during tuning. If the tuned choice is the unweighted control that is a
# valid, honest outcome and is reported as such.
_HALFLIFE_GRID: list[float | None] = [90.0, 180.0, 365.0, 730.0, None]


def recency_weights(observation_dates: pd.Series, halflife_days: float | None,
                    t_ref: "pd.Timestamp | None" = None) -> np.ndarray:
    """Per-row exponential recency weight ``w = 0.5 ** (age_days / halflife)``.

    ``age_days`` is measured from ``t_ref`` (default = the MAX ObservationDate in
    ``observation_dates``) so the most-recent training row has weight 1.0 and
    older rows decay. POINT-IN-TIME: ``t_ref`` must be the max of the CURRENT
    train window only — never a future/global max — so this stays leakage-safe
    when called per fold (the caller passes the fold's train slice, or its inner
    train slice during tuning).

    ``halflife_days=None`` (or non-positive) is the UNWEIGHTED control: all-ones
    weights, i.e. exactly the pre-v14 fit. This equivalence is pinned by a test.
    """
    n = len(observation_dates)
    if halflife_days is None or halflife_days <= 0:
        return np.ones(n, dtype=float)
    ref = observation_dates.max() if t_ref is None else t_ref
    age = (ref - observation_dates).dt.days.to_numpy(dtype=float)
    return 0.5 ** (age / float(halflife_days))


def _tune_halflife(df_tr: pd.DataFrame, X_tr: pd.DataFrame, y_tr: pd.Series,
                   grid: list | None = None):
    """Leakage-safe half-life selection on inner splits of the fold's TRAIN
    window. Holds out the most-recent ~15% of the train window (purged: an inner-
    train row is dropped if its harvest label date falls on/after the inner
    cutoff) as an inner-validation block, fits the pooled gated model at each grid
    half-life with recency weights computed on the INNER-TRAIN window only, and
    picks the half-life with the lowest inner-val MAE. NEVER touches the fold's
    test block.

    Returns ``(best_halflife, inner_scores)`` where ``inner_scores`` maps each
    grid value (float or None) to its inner-val MAE (or None if the inner split
    was degenerate / could not be scored). If the inner split itself is
    degenerate (too few dates/rows), returns ``(None, {})`` — the caller then
    uses the unweighted control, reported honestly.
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
    # Pick the lowest inner-val MAE. Tie-break toward the unweighted control
    # (None) to avoid adding weighting on noise, else toward the longer half-life
    # (gentler weighting) — both conservative.
    best = min(scores, key=lambda k: (scores[k],
                                      0 if k is None else 1,
                                      -(k or 0)))
    return best, scores


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


# Minimum count of labelled observations for a crop's OWN quantiles to be trusted
# as an adequate-history (non-cold-start) prior. See DECISIONS.md 2026-07-04 for
# the original justification on the PRE-WIDENING frame (then-bimodal: 7 crops at
# 167-328 rows vs 4 HARTI-backed crops at ~2,635-2,691 rows; on the widened
# 82-crop store the v13 gate admits 19 crops at >=365 labelled rows).
# 365 (~one calendar year of daily rows) cleanly separates the two clusters with a
# large margin and marks the floor at which per-crop quantiles can span a full
# Yala+Maha seasonal cycle. Persisted into the payload (configurable there, not via
# env) so serving reads it without a code change.
#
# v13 (2026-07-08) — this SAME constant is the history GATE that decides which
# crops the pooled ML model is trained on and served for. The v13.1 post-mortem
# showed the pooled model beats recency-mean on long-history (>=365 labelled rows)
# crops but loses badly on thin (<365) crops in a rising regime, so the shipped
# predictor is a HYBRID: pooled model on gated (long-history) crops, recency-mean
# baseline on thin crops. There must be ONE definition of 365 (this constant),
# NOT a forked second copy in the gate — dataset/serving both read it from here or
# from the persisted payload. PROPOSED default — owner signs off.
_DEFAULT_MIN_HISTORY_OBS = 365


def history_gated_crops(df: pd.DataFrame,
                        min_history_obs: int = _DEFAULT_MIN_HISTORY_OBS) -> set:
    """Deterministic history gate: the set of CropIds (as-is dtype from ``df``)
    with at least ``min_history_obs`` LABELLED rows in ``df``.

    This is the v13 model-served set. Callers MUST pass a POINT-IN-TIME frame:
      * during walk-forward CV, ``df`` = the fold's TRAIN slice, so a crop is
        gated in only if it already had >=min rows at the fold's train cutoff
        (no future rows counted — honest, no lookahead);
      * at final-fit time, ``df`` = the full labelled training frame.

    Purely a row count over ``df`` — no model, no target peeking, fully
    deterministic. The single 365 definition is ``_DEFAULT_MIN_HISTORY_OBS``;
    this function never forks a second literal.
    """
    counts = df.groupby("CropId").size()
    return set(counts[counts >= int(min_history_obs)].index)


def _incumbent_hybrid_mae() -> float | None:
    """CV hybrid MAE recorded by the currently-promoted version, if any.

    v14 must beat the INCUMBENT hybrid (not just a naive baseline). The value is
    read from the live promoted metadata's cv block. Returns None when nothing is
    promoted yet or the incumbent predates the hybrid field — the incumbent gate
    then becomes a no-op and the naive-baseline gate governs alone.
    Same-regime by construction: the v14 store == the v13 store (v14 changes the
    FIT, not the data), so the incumbent's recorded hybrid MAE is measured on the
    same rows/folds as v14's.
    """
    from ..registry import registry
    live = registry.load_promoted_metadata()
    if not live:
        return None
    cv = live.get("cv") or {}
    v = cv.get("hybrid_MAE")
    return float(v) if v is not None else None


def _incumbent_gate(hybrid_mae: float) -> tuple[bool, float | None]:
    """(beats_incumbent, incumbent_hybrid_mae). Brick-safe: when nothing is
    promoted or the incumbent predates the hybrid field, the gate is a NO-OP
    (True, None) — it must never fail-closed into a state where nothing can
    ever be promoted."""
    incumbent = _incumbent_hybrid_mae()
    return (incumbent is None or hybrid_mae < incumbent), incumbent


def _blend_winner_crops(df_tr, X_tr, y_tr):
    """Leakage-safe per-crop selection: hold out the most-recent slice of TRAIN as
    an inner validation set, fit model + crop-mean on the earlier part, and return
    the set of CropIds where the model beats crop-mean on that inner val. The real
    fold then serves the model only for those crops. Uses NO test data.

    NOTE (v13): superseded as the SHIPPED per-crop selector by the deterministic
    ``history_gated_crops`` gate (an inner-val tournament with 1-2 events/fold was
    noisy). Retained because it still backs the reporting-only per-crop "blend"
    candidate in the walk-forward diagnostics."""
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

        # --- v13 HYBRID candidate (the shipped predictor) --------------------
        # POINT-IN-TIME history gate: count TRAIN rows only (no future peek), so
        # a crop is model-served this fold iff it already had >=365 labelled rows
        # at the fold's train cutoff. The pooled model is FIT ON GATED CROPS ONLY
        # (its served set == its train set — generalises _blend_winner_crops with
        # the deterministic gate replacing the inner-val tournament). Thin crops
        # fall to the recency-mean baseline. This is EXACTLY what production serves
        # (serving routes non-gated crops to the fallback ladder), so the gate
        # measures the true served predictor.
        gated = history_gated_crops(dtr, _DEFAULT_MIN_HISTORY_OBS)
        g_tr = dtr["CropId"].isin(gated).to_numpy()
        use_model = dte["CropId"].isin(gated).to_numpy()
        # v14: tune the recency half-life on INNER splits of the GATED train
        # window only (leakage-safe — never sees this fold's test block). Then fit
        # the pooled gated model with recency weights at the chosen half-life,
        # computed on the (full) gated train window with its own max as t_ref.
        chosen_hl, inner_scores = None, {}
        exp_thin_weighted_mae = None
        if g_tr.sum() >= 50 and use_model.any():
            dtr_g, Xtr_g, ytr_g = dtr[g_tr], Xtr[g_tr], ytr[g_tr]
            chosen_hl, inner_scores = _tune_halflife(dtr_g, Xtr_g, ytr_g)
            w_g = recency_weights(dtr_g["ObservationDate"], chosen_hl)
            hybrid_model_pred = _fit_predict_log(
                make_model(0.5), Xtr_g, ytr_g, Xte, sample_weight=w_g)
            # --- exploratory (report-only): weighted pooled model on THIN crops.
            # Fit the pooled model on ALL train crops (thin included) with recency
            # weights at the same chosen half-life, score the thin test segment.
            # Evidence for a FUTURE served-set widening; changes NO behavior.
            if (~use_model).any():
                w_all = recency_weights(dtr["ObservationDate"], chosen_hl)
                thin_pred_all = _fit_predict_log(
                    make_model(0.5), Xtr, ytr, Xte, sample_weight=w_all)
                yte_thin = yte.to_numpy(dtype=float)[~use_model]
                exp_thin_weighted_mae = float(
                    np.mean(np.abs(thin_pred_all[~use_model] - yte_thin)))
        else:
            # Degenerate fold (no gated crops / too little gated train) -> the
            # hybrid is all-baseline. Keep the array shape.
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
        # Exploratory thin-segment: recency-mean baseline MAE on the SAME thin
        # rows, for a like-for-like weighted-pooled-vs-recency-mean comparison.
        exp_thin_recmean_mae = (
            regression_metrics(yte_np[~use_model], rw_pred[~use_model])["MAE"]
            if (~use_model).any() else None)

        # --- candidate approaches (leakage-safe) -----------------------------
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

        # --- directional accuracy (go/no-go signal, REPORTING ONLY) ----------
        # Reference = price KNOWN AT observation date (AvgPrice), never future.
        # This is the carry-forward reference; carry-forward's own predicted move
        # is therefore always 0 (flat) -> its directional_acc is degenerate but
        # recorded honestly for comparison.
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
                 # v14 recency-weighting: chosen half-life this fold + the inner-
                 # tuning scores (grid val -> inner-val MAE). None == unweighted
                 # control chosen (or inner split degenerate).
                 recency_halflife=chosen_hl,
                 recency_inner_scores={("inf" if k is None else k): round(v, 3)
                                       for k, v in inner_scores.items()},
                 # v14 exploratory (report-only): weighted pooled model vs recency-
                 # mean on the THIN (<365) test segment. Evidence for a future
                 # served-set widening; NO behavior change this iteration.
                 exp_thin_weighted_MAE=exp_thin_weighted_mae,
                 exp_thin_recmean_MAE=exp_thin_recmean_mae)
        fold_rows.append(m)
    return fold_rows


def _crop_fallback(df: pd.DataFrame) -> dict:
    """Per-crop harvest-price quantiles + category + global fallback — the
    deployable baseline ladder used when no ML model is good enough to promote,
    and the graceful-degradation prior for thin / unknown crops.

    Additive schema (old serving code degrades if any key is absent):
      per_crop[cid]         : {p10,p50,p90, n_obs}   (n_obs = labelled row count)
      by_category[cat]      : {p10,p50,p90, n_obs}   (pooled category quantiles)
      global                : {p10,p50,p90}
      min_history_obs       : int threshold for "adequate own history"
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

    # Candidate ML predictors. Each is a leakage-safe approach evaluated on the
    # same folds. v13: the SHIPPED predictor is the `hybrid` (history-gated pooled
    # model on >=365-row crops, recency-mean baseline on thin crops). It is the
    # candidate we PROMOTE ON because it is the one production actually serves —
    # `served_ml_kind` stays "model" (the gated crops go through the pooled path)
    # and the routing to fallback for non-gated crops is done at serve time via
    # `served_on_crops`. `pooled model`/`residual`/`blend` remain reporting-only
    # candidates for comparison in metadata.
    ml_candidates = {"model": model_mae, "residual": residual_mae,
                     "blend": blend_mae, "hybrid": hybrid_mae}
    best_ml_name = "hybrid"                      # v13: hybrid is the served predictor
    best_ml_mae = hybrid_mae

    beats_baseline = best_ml_mae < best_baseline_mae

    # --- v14 incumbent gate: must also beat the promoted v13 hybrid MAE -------
    # v14 (recency-weighted gated fit) is only shippable if it improves on the
    # INCUMBENT v13 hybrid, not merely on the recency-mean baseline. Same
    # 3-fold purged walk-forward, same rows/denominator — v14 changes only the
    # FIT. Brick-safe no-op when nothing is promoted / older shape (see
    # _incumbent_gate). Deliberately does NOT flip beats_baseline: that flag
    # (and everything derived from it — active_predictor, payload) must keep
    # telling the true baseline story; losing to the incumbent only blocks
    # PROMOTION, decided at the guardrail below.
    beats_incumbent, _incumbent_mae = _incumbent_gate(hybrid_mae)

    # --- v13 fold corridor: guard against a pass driven by fold averaging -----
    # The gate is not just CV MAE < best baseline; the SHIPPED hybrid must also
    # not regress vs recency-mean in the volatile fold-3 regime, and the long-
    # history (model-served) segment must not blow up. These are WITHIN-FOLD
    # checks (never cross-regime absolute MAE). Recorded and surfaced; a corridor
    # breach flips beats_baseline off so we do not promote a fold-noise pass.
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
        # Long-history segment must not regress vs the v12 long segment (~77.43).
        # Allow a small tolerance for seed/fold-boundary jitter.
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
            # Distinguish the failure mode: baseline gate vs v14 incumbent gate
            # vs corridor. Do NOT say "worse than baseline" when v14 actually
            # beat the baseline but lost to the incumbent (honest reporting).
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
        # Festival-feature honesty (R1.1 P2): the festival columns
        # (HarvestInFestivalLeadup / DaysFromHarvestToNextFestival /
        # DaysToNextFestivalAny / InLeadupAvurudu / InLeadupChristmas) are added
        # on a DOMAIN PRIOR + leakage-safety, NOT CV-proven lift. With only ~10
        # festival EVENTS per festival in the whole corpus (and 1-2 per CV fold),
        # per-festival price lift is statistically UNVERIFIABLE until post-P3.
        # Success here = correctness + leakage-safety + Prophet-readiness, not a
        # CV MAE improvement -- do not read fold noise as festival signal.
        print("\nNote: festival features are domain-prior + leakage-safe, NOT "
              "CV-proven (too few events until post-P3).")
        # Macro-feature honesty (R1.1 P3): the CBSL macro columns
        # (MacroFoodInflationYoY / MacroFoodImportsYoY / MacroPolicyRateOPR) are
        # added on leakage-safety (as-of on the PublishedAt vintage date, 60-day
        # staleness cap, NaN-not-0) + a DOMAIN PRIOR, NOT CV-proven lift. They are
        # NATIONAL series -- identical across crops on a given date -- so they
        # carry NO cross-sectional signal and expected CV lift is ~0. Per-feature
        # lift is statistically UNVERIFIABLE at the current ~13 months of history
        # (1-2 macro vintages per CV fold). Do not read fold noise as macro signal
        # or promote on it.
        print("Note: macro features are national + leakage-safe, NOT CV-proven "
              "(no cross-sectional signal; expected lift ~0).")

    # v13 final history gate: count on the FULL labelled training frame (the
    # point-in-time frame at final-fit time is all of the training data). This is
    # the served set threaded into the payload; serving routes any crop NOT in it
    # to the fallback ladder. Lowercased GUID strings to match serving's crop_id
    # normalisation (predict.py lower-cases before every lookup).
    gated_final = history_gated_crops(df, _DEFAULT_MIN_HISTORY_OBS)
    served_on_crops = sorted(str(c).lower() for c in gated_final)
    g_final_mask = df["CropId"].isin(gated_final).to_numpy()
    if verbose:
        print(f"\nHistory gate (final fit, >= {_DEFAULT_MIN_HISTORY_OBS} labelled "
              f"rows): {len(served_on_crops)} model-served crops / "
              f"{df['CropId'].nunique() - len(served_on_crops)} fallback crops.")

    # Final quantile models: FIT ON GATED CROPS ONLY (the model's served set ==
    # its train set — v13 history-gated hybrid). Non-gated crops are neither in
    # the fit nor in served_on_crops, and are routed to the fallback ladder at
    # serve time. Fitting on gated-only also keeps the pooled encoder's category
    # set == served_on_crops, so a non-served CropId can never reach the model.
    X_g, y_g = X[g_final_mask].copy(), y[g_final_mask]
    df_g = df[g_final_mask]
    # Drop unused CropId categories so the categorical encoder's known set is
    # exactly the gated crops (unseen-category raise stays the crash-guard's job).
    for c in dataset.CATEGORICAL_COLS:
        if c in X_g.columns and str(X_g[c].dtype) == "category":
            X_g[c] = X_g[c].cat.remove_unused_categories()
    # v14: tune the recency half-life on inner splits of the FULL gated training
    # frame (leakage-safe: inner split has no test block at final-fit time — this
    # IS all training data), then apply the SAME half-life's recency weights to
    # EVERY quantile head so p10/p50/p90 stay consistent. t_ref = the gated
    # frame's max ObservationDate (most-recent train row => weight 1.0).
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

    # Final residual quantile models (the "residual" kind). Each predicts the
    # multiplicative residual log1p(price) - log1p(offset); serving adds the
    # offset back: expm1(model_pred + log1p(offset)). The offset is a per-crop
    # mean computed from ALL labelled TRAIN data and PERSISTED below, so serving
    # never recomputes it from future data -> point-in-time / leakage-safe.
    # Offset is an additive log-space shift, identical across quantiles.
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

    # --- Per-crop fallback-predictor selection (chip task_9b1cd894) -----------
    # For every fallback-served crop (NOT in served_on_crops), pick the fallback
    # point predictor that serves it best on a leakage-safe purged walk-forward
    # backtest over {recency-mean incumbent, carry-forward challenger}. A crop is
    # switched away from the recency-mean incumbent ONLY if the challenger beats it
    # by >=10% MAE on >=30 origins AND does not regress vs the category-median tier
    # serving actually deploys today. The winning choices ride (signed) in the
    # payload under fallback["choice"]; serving reads them and FAILS CLOSED to the
    # recency-mean incumbent for any crop absent from the map. Only carry-forward
    # is SHIPPED (trivially servable = last observed AvgPrice); seasonal-naive is
    # evaluated + reported (see the offline experiment) but not wired to serving.
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
               # v14 incumbent gate: v14 must beat BOTH the naive baseline AND the
               # promoted v13 hybrid MAE (same rows/folds; v14 changes only the fit).
               "incumbent_hybrid_MAE": _incumbent_mae,
               "beats_incumbent": beats_incumbent,
               # Directional accuracy (go/no-go signal) -- REPORTING ONLY, not a
               # gate input. Pooled = mean over folds (folds carry per-fold
               # *_dir_acc keys). Reference price = AvgPrice known at obs date.
               # carry_forward's move is always flat so its dir_acc is degenerate
               # (recorded for honesty). ADDITIVE keys: the pre-existing flat cv
               # schema is unchanged.
               "dir_acc": {
                   "model": fmean_dir("model_dir_acc"),
                   "residual": fmean_dir("residual_dir_acc"),
                   "blend": fmean_dir("blend_dir_acc"),
                   "carry_forward": fmean_dir("carry_dir_acc"),
                   "crop_mean": fmean_dir("cropmean_dir_acc"),
                   "recency_weighted_mean": fmean_dir("recencymean_dir_acc"),
               }},
        "beats_baseline": beats_baseline,
        # active_predictor describes the SHIPPED predictor for humans: "hybrid"
        # (gated pooled model + recency-mean fallback) when promoted, else the
        # crop-mean fallback ladder.
        "active_predictor": ("hybrid" if beats_baseline else "crop_mean_fallback"),
        # served_ml_kind is the ARTIFACT kind serving uses for gated crops. The
        # hybrid's gated crops go through the pooled "model" path; the fallback
        # routing for non-gated crops is done via served_on_crops, NOT via a new
        # ml_kind (serving only honors {"model","residual"}). Keep it "model".
        "served_ml_kind": "model",
        # v13 history gate: the crops the ML model is served for. Non-served crops
        # route to the fallback ladder at serve time. Lowercased GUID strings.
        "served_on_crops": served_on_crops,
        "n_served_crops": len(served_on_crops),
        # Per-crop fallback-predictor selection (chip task_9b1cd894). The MAP is
        # also in the (signed) payload under fallback["choice"]; this metadata
        # block is the human-auditable record of the decision + gate numbers.
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
        # v14: exponential recency sample-weighting on the pooled gated fit. The
        # half-life is tuned leakage-safe on inner splits of the (gated) train
        # window; None == the unweighted control was chosen. Recorded for audit
        # and reproducibility — the WEIGHTS themselves are a deterministic
        # function of ObservationDate + this half-life (see recency_weights).
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
               # served_ml_kind tells serving which artifact path to use; residual
               # artifacts are persisted so serving can honor served_ml_kind="residual".
               "served_ml_kind": "model",
               # v13: the model-served crop set. Serving MUST route a crop not in
               # this set to the fallback ladder (intentional, not exception-driven).
               "served_on_crops": served_on_crops,
               # v14: half-life used for the recency-weighted gated fit (audit
               # only; the fitted models already bake in the weights). Serving
               # does NOT need to recompute weights — additive, ignorable field.
               "recency_halflife_days": final_halflife,
               "residual_models": residual_models,
               "residual_offsets": residual_offsets,
               "residual_offset_global": residual_offset_global}

    # --- Retrain guardrail: never regress the live predictor ------------------
    # A retrain must NOT make serving worse. We always register the new version
    # for history, but we only move promoted.json when the new candidate is
    # STRICTLY better than what is currently live -- measured by the CV MAE of
    # whichever predictor each version actually serves (ML model vs crop-mean
    # fallback). If it is not better, the previously-promoted version stays
    # active (no-op promotion / rollback).
    # The ML path's served MAE is the best leakage-safe ML candidate; the fallback
    # path's served MAE is the best baseline.
    # v14 incumbent short-circuit: a candidate that honestly beats its baselines
    # but not the live incumbent's hybrid MAE is NOT promoted — with its own
    # explicit reason, so _promotion_decision's baseline-centric wording is never
    # borrowed for an incumbent loss. beats_baseline stays true in the metadata.
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
    """Decide whether the freshly-trained candidate should become the live
    predictor. Returns (promote: bool, human_reason: str).

    REGIME-AWARE rule (fixed 2026-06-29). The previous version compared the
    candidate's served-MAE against the live version's *recorded* served-MAE.
    That is INVALID across a data-regime change: a corpus expansion (e.g. the
    HARTI backfill, ~1 -> ~10 seasonal cycles) changes the walk-forward test
    distribution, so absolute MAE numbers between versions are not comparable
    (v3 crop-mean scored 94.64 on ~1 cycle; on the 10-yr folds even crop-mean
    scores ~136). Comparing 100.51 (10-yr) vs 94.64 (1-cycle) wrongly blocked a
    model that beats EVERY baseline on its own folds.

    The trainer already computes the only sound, same-regime comparison: the
    within-fold gate `beats_baseline` (best ML strictly < best baseline on the
    SAME folds, same data). We decide on THAT, not on cross-version MAE.

    Rules:
      * If nothing is promoted yet, promote (serving needs an active version).
      * If the candidate's ML path beats its own baselines (beats_baseline=True),
        PROMOTE: it is provably better than every baseline on the current data,
        including the crop-mean the incumbent serves. Cross-version MAE is
        irrelevant here.
      * If the candidate does NOT beat its baselines, it would serve the same
        crop-mean fallback as before. Then only re-promote when there is no live
        version, or when the live version is ALSO a non-beating fallback AND the
        candidate's own-fold crop-mean MAE is strictly lower than the live one
        (a like-for-like fallback comparison). Otherwise keep the incumbent.
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
