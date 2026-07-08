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
        if g_tr.sum() >= 50 and use_model.any():
            hybrid_model_pred = _fit_predict_log(
                make_model(0.5), Xtr[g_tr], ytr[g_tr], Xte)
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
                 hybrid_long_seg_MAE=seg_long, hybrid_thin_seg_MAE=seg_thin)
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
            print(f"  fold {i}: train={f['train']} test={f['test']} "
                  f"HYBRID={f['hybrid_MAE']}(served {f['hybrid_model_frac']}, "
                  f"{f['n_gated_crops']} crops; long-seg={f['hybrid_long_seg_MAE']} "
                  f"thin-seg={f['hybrid_thin_seg_MAE']}) "
                  f"| pooled={f['MAE']} resid={f['residual_MAE']} "
                  f"blend={f['blend_MAE']} "
                  f"| cropmean={f['cropmean_MAE']} recmean={f['recencymean_MAE']} "
                  f"carry={f['carry_MAE']}")
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
        if corridor_notes:
            print("Corridor breaches   : " + "; ".join(corridor_notes))
        else:
            print("Corridor            : OK (final-fold not worse than recency-mean; "
                  "long-history segment within ceiling)")
        verdict = (f"PROMOTE ({best_ml_name} beats {best_baseline_name}, corridor OK)"
                   if beats_baseline
                   else f"DO NOT PROMOTE ('{best_ml_name}' worse than "
                        f"'{best_baseline_name}' or corridor breach) -> serve fallback")
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
    # Drop unused CropId categories so the categorical encoder's known set is
    # exactly the gated crops (unseen-category raise stays the crash-guard's job).
    for c in dataset.CATEGORICAL_COLS:
        if c in X_g.columns and str(X_g[c].dtype) == "category":
            X_g[c] = X_g[c].cat.remove_unused_categories()
    final = {q: make_model(a) for q, a in QUANTILES.items()}
    for q, mdl in final.items():
        mdl.fit(X_g, np.log1p(y_g))

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
        "min_history_obs": _DEFAULT_MIN_HISTORY_OBS,
        "n_train_rows": int(len(df)),
        "n_crops": int(df["CropId"].nunique()),
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
