"""Per-crop fallback-predictor SELECTION (chip task_9b1cd894).

The v13+ hybrid serves a naive fallback for every crop below the history gate
(`served_on_crops`). The *incumbent* fallback point predictor is the
recency-weighted crop mean (`baselines.recency_weighted_crop_mean_pred`) — the
same predictor the promotion gate scores thin crops with. Fruit-history
experiments (2026-07-17) showed that predictor mis-serves thin, VOLATILE fruit
crops badly (Kolikuttu-class: recency-mean MAE ~144 vs carry-forward ~30 on
matched origins): the fallback CHOICE, not the history length, is the defect.

This module picks, PER fallback-served crop, the fallback point predictor that
serves it best on a leakage-safe purged walk-forward backtest, and returns a
map of the crops that should switch AWAY from the recency-mean incumbent. The
map is shipped (signed) in the model payload; serving reads it and fails CLOSED
to the incumbent for any crop absent from the map / any old payload / any error.

LEAKAGE DISCIPLINE (identical to the hybrid gate):
  * fold structure is the SAME expanding-window purged walk-forward as
    ``model.purged_walk_forward`` (test = last 40% of unique obs dates, split
    into ``n_folds`` blocks; a train row is purged if its harvest label date
    falls on/after the test window start);
  * every candidate predictor for a fold's test rows is computed from that
    fold's TRAIN slice only (recency-mean) or from information known at the
    observation date (carry-forward = AvgPrice at obs; seasonal-naive = a price
    at/before HarvestDate-365, guarded so it is strictly < obs);
  * the resulting per-crop choice is a static function of the training data,
    so at serve time it is fully point-in-time (serving just reads the map).

SWITCH GATES (hub-set):
  * switch a crop away from recency-mean ONLY IF a challenger beats it by
    >= ``switch_margin`` (default 10%) MAE on that crop's matched walk-forward
    origins AND the crop has >= ``min_origins`` (default 30) evaluable origins;
  * AGGREGATE: the fallback-segment pooled (origin-weighted) MAE with the
    switches applied must not be worse than the all-recency-mean pooled MAE, and
    no switched crop may be worse than its own recency-mean baseline (the per-
    crop >=10% gate already guarantees this; asserted). If the aggregate gate
    fails, NO crop switches (empty map) — a null result is a valid outcome.
"""
from __future__ import annotations

import numpy as np
import pandas as pd

from . import baselines

# Incumbent = the predictor a fallback crop gets today (and the fail-closed
# default at serve time). Named so serving and the trainer agree on one string.
INCUMBENT = "recency_mean"
# Challenger predictors, in priority order for tie-breaking (cheaper / more
# robust first). seasonal-naive is included only where it is leakage-safe.
CHALLENGERS = ("carry_forward", "seasonal_naive")

DEFAULT_MIN_ORIGINS = 30
DEFAULT_SWITCH_MARGIN = 0.10


def _seasonal_naive_pred(df_all: pd.DataFrame, test_df: pd.DataFrame) -> np.ndarray:
    """y at (HarvestDate - 365d): the crop's AvgPrice on/before one year before
    the harvest target, via a per-crop backward as-of over the observed AvgPrice
    series. NaN where no such prior price exists.

    LEAKAGE GUARD: the as-of source price must be knowable at the observation
    date. HarvestDate-365 = obs + gp - 365, which is strictly < obs only when
    gp < 365. For gp >= 365 the target date is >= obs, so a backward as-of could
    pick a price observed AFTER obs -> lookahead. Those rows are set to NaN
    (seasonal-naive is simply not offered for >=1yr-horizon crops).
    """
    src = (df_all[["CropId", "ObservationDate", "AvgPrice"]]
           .dropna(subset=["AvgPrice"]).sort_values("ObservationDate"))
    out = np.full(len(test_df), np.nan)
    gp = test_df["GrowthPeriodDays"].astype(float).to_numpy()
    tgt = (test_df["HarvestDate"] - pd.Timedelta(days=365))
    safe = gp < 365.0  # only these are leakage-safe (target date strictly < obs)
    for cid, idx in test_df.groupby("CropId").groups.items():
        s = src[src["CropId"] == cid]
        if s.empty:
            continue
        left = pd.DataFrame({"t": tgt.loc[idx].to_numpy(),
                             "_pos": [test_df.index.get_loc(i) for i in idx],
                             "_safe": [safe[test_df.index.get_loc(i)] for i in idx]}
                            ).sort_values("t")
        merged = pd.merge_asof(left, s.rename(columns={"ObservationDate": "t"}),
                               on="t", direction="backward")
        vals = np.array(merged["AvgPrice"].to_numpy(), dtype=float)
        vals[~merged["_safe"].to_numpy(dtype=bool)] = np.nan
        out[merged["_pos"].to_numpy()] = vals
    return out


def _fold_blocks(df: pd.DataFrame, n_folds: int) -> list:
    obs = df["ObservationDate"]
    uniq = np.sort(obs.unique())
    test_region = uniq[int(len(uniq) * 0.6):]
    return [b for b in np.array_split(test_region, n_folds) if len(b)]


def _candidate_preds(df_all: pd.DataFrame, dtr: pd.DataFrame,
                     dte: pd.DataFrame) -> dict[str, np.ndarray]:
    """All fallback candidate predictions for a fold's test rows, leakage-safe."""
    return {
        "recency_mean": baselines.recency_weighted_crop_mean_pred(dtr, dte),
        "carry_forward": baselines.carry_forward_pred(dte),
        "seasonal_naive": _seasonal_naive_pred(df_all, dte),
    }


def _serving_incumbent_maps(df: pd.DataFrame):
    """The fallback distributions serving ACTUALLY deploys today, computed from the
    FULL labelled frame exactly like ``train.model._crop_fallback`` (per-crop and
    per-category p50 medians of LabelHarvestPrice). These are the REAL current
    serving incumbents for a <365-row fallback crop: it is served the CATEGORY
    median tier (crop tier needs >=min_history_obs rows == the model gate), so
    ``category_median`` is what a switch must actually beat to be a real win.
    Returns (crop_median: {cid_lower: p50}, cat_median: {cat: p50}, cat_of_crop:
    {cid_lower: cat}, global_p50)."""
    from ..serving.crop_categories import category_for

    y = pd.to_numeric(df["LabelHarvestPrice"], errors="coerce")
    crop_median = {str(c).lower(): float(v)
                   for c, v in df.assign(_y=y).groupby("CropId")["_y"].median().items()}
    names = (df.groupby("CropId")["CropName"].first().to_dict()
             if "CropName" in df.columns else {})
    cat_of_crop: dict[str, str] = {}
    for cid in df["CropId"].unique():
        c = category_for(str(cid).lower(), names.get(cid))
        if c:
            cat_of_crop[str(cid).lower()] = c
    tmp = df.assign(_y=y, _cat=df["CropId"].map(
        lambda c: cat_of_crop.get(str(c).lower()))).dropna(subset=["_cat"])
    cat_median = {str(k): float(v)
                  for k, v in tmp.groupby("_cat")["_y"].median().items()}
    return crop_median, cat_median, cat_of_crop, float(y.median())


def _collect_walk_forward(df: pd.DataFrame, fallback_crops: set,
                          n_folds: int, incumbents: tuple) -> pd.DataFrame:
    """Run the purged walk-forward and collect per (crop, test-row) candidate
    predictions vs the harvest label, for FALLBACK crops only. Returns a long
    frame with columns: CropId, y, <one column per candidate>."""
    crop_median, cat_median, cat_of_crop, global_median = incumbents
    obs = df["ObservationDate"]
    label_date = obs + pd.to_timedelta(df["GrowthPeriodDays"].astype(int), unit="D")
    y_all = pd.to_numeric(df["LabelHarvestPrice"], errors="coerce").astype(float)
    rows = []
    for block in _fold_blocks(df, n_folds):
        t_start = pd.Timestamp(block.min())
        test_mask = obs.isin(block).to_numpy()
        train_mask = ((obs < t_start) & (label_date < t_start)).to_numpy()
        if train_mask.sum() < 80 or test_mask.sum() < 20:
            continue
        dtr = df[train_mask]
        dte = df[test_mask]
        fb = dte["CropId"].isin(fallback_crops).to_numpy()
        if not fb.any():
            continue
        preds = _candidate_preds(df, dtr, dte)
        sub = pd.DataFrame({
            "CropId": dte["CropId"].to_numpy()[fb],
            "y": y_all[test_mask].to_numpy()[fb],
        })
        for name, arr in preds.items():
            sub[name] = np.asarray(arr, dtype=float)[fb]
        # Real serving incumbents (STATIC full-frame p50, mapped by crop): what
        # serving deploys today. Optimistic (uses full-frame info) so beating them
        # on the honest walk-forward challenger is a CONSERVATIVE no-regression bar.
        cids_lower = pd.Series(dte["CropId"].to_numpy()[fb]).astype(str).str.lower()
        sub["crop_median_serving"] = cids_lower.map(crop_median).to_numpy()
        sub["category_median_serving"] = cids_lower.map(
            lambda c: cat_median.get(cat_of_crop.get(c), global_median)).to_numpy()
        rows.append(sub)
    if not rows:
        return pd.DataFrame(columns=["CropId", "y", "recency_mean",
                                     "carry_forward", "seasonal_naive",
                                     "crop_median_serving", "category_median_serving"])
    return pd.concat(rows, ignore_index=True)


def _mae(y: np.ndarray, p: np.ndarray) -> "tuple[float | None, int]":
    """MAE on the rows where BOTH y and p are finite, plus that matched count."""
    y = np.asarray(y, float)
    p = np.asarray(p, float)
    m = np.isfinite(y) & np.isfinite(p)
    n = int(m.sum())
    if n == 0:
        return None, 0
    return float(np.mean(np.abs(y[m] - p[m]))), n


# Which challengers may actually be SHIPPED into the serving choice map. Default
# = carry-forward only: it is trivially servable (the last observed AvgPrice is
# already on the feature row serving fetches) with no new query and no serve-time
# leakage guard. seasonal-naive is EVALUATED and REPORTED (it wins bigger on some
# crops) but NOT shipped here — wiring a serve-time 1-year-lookback query is a
# separate, larger serving change (documented as future upside in RESULTS.md).
DEFAULT_SERVABLE_CHALLENGERS = ("carry_forward",)


def select_fallback_choices(df: pd.DataFrame, served_on_crops: set | list,
                            *, n_folds: int = 3,
                            min_origins: int = DEFAULT_MIN_ORIGINS,
                            switch_margin: float = DEFAULT_SWITCH_MARGIN,
                            servable_challengers: tuple = DEFAULT_SERVABLE_CHALLENGERS):
    """Pick the best fallback point predictor per fallback-served crop.

    ``served_on_crops``: the model-served crop set (lower-cased GUID strings or
    raw CropIds) — these are EXCLUDED (scope guard: never touch model-served
    routing here). Every other crop with labelled rows is a fallback crop.

    ``servable_challengers``: the subset of challengers eligible for the SHIPPED
    choice map (serving must be able to compute them). All of ``CHALLENGERS`` are
    still evaluated + reported; only these can be selected for a switch.

    Returns ``(choice_map, table, aggregate)``:
      * ``choice_map``: ``{crop_id_lower: challenger_name}`` ONLY for crops that
        pass the switch gates. Absence => recency-mean incumbent (fail-closed).
      * ``table``: list of per-crop dicts (crop, code, n_origins, per-candidate
        MAE, best challenger, delta%, switched) for the report.
      * ``aggregate``: pooled gate numbers + whether switches were applied.
    """
    served_lower = {str(c).lower() for c in served_on_crops}
    all_crops = set(df["CropId"].unique())
    fallback_crops = {c for c in all_crops if str(c).lower() not in served_lower}

    incumbents = _serving_incumbent_maps(df)
    long = _collect_walk_forward(df, fallback_crops, n_folds, incumbents)
    names = (df.groupby("CropId")["CropName"].first().to_dict()
             if "CropName" in df.columns else {})
    codes = (df.groupby("CropId")["CropCode"].first().to_dict()
             if "CropCode" in df.columns else {})

    table: list[dict] = []
    choice_map: dict[str, str] = {}
    for cid in sorted(fallback_crops, key=lambda c: str(c)):
        g = long[long["CropId"] == cid] if len(long) else long
        rec_mae, rec_n = _mae(g["y"], g["recency_mean"]) if len(g) else (None, 0)
        # Real current serving incumbent = the category-median tier (a <365-row
        # fallback crop never reaches crop tier); crop-median reported too.
        cat_mae, _ = _mae(g["y"], g["category_median_serving"]) if len(g) else (None, 0)
        crop_med_mae, _ = _mae(g["y"], g["crop_median_serving"]) if len(g) else (None, 0)
        row: dict = {
            "cropId": str(cid).lower(),
            "cropCode": codes.get(cid),
            "cropName": names.get(cid),
            "n_origins": rec_n,
            "recency_mean_MAE": rec_mae,
            "serving_category_MAE": cat_mae,
            "serving_cropmedian_MAE": crop_med_mae,
        }
        # Per-challenger matched MAE + coverage (matched = y, incumbent AND
        # challenger all finite). Selection is COVERAGE-AWARE: a challenger only
        # competes to be "best" if it has >= min_origins matched rows, so a
        # sparse-but-lucky seasonal-naive can never shadow a robust carry-forward.
        ch_stats: dict[str, tuple] = {}       # name -> (mae, n_match)
        for ch in CHALLENGERS:
            if ch not in g.columns:
                continue
            m = (np.isfinite(g["y"].to_numpy()) & np.isfinite(g["recency_mean"].to_numpy())
                 & np.isfinite(g[ch].to_numpy()))
            n_match = int(m.sum())
            ch_mae = (float(np.mean(np.abs(g["y"].to_numpy()[m] - g[ch].to_numpy()[m])))
                      if n_match else None)
            row[f"{ch}_MAE"] = ch_mae
            row[f"{ch}_n"] = n_match
            ch_stats[ch] = (ch_mae, n_match)

        def _best(among: tuple):
            cand = [(name, mae, n) for name, (mae, n) in ch_stats.items()
                    if name in among and mae is not None and n >= min_origins]
            return min(cand, key=lambda t: t[1]) if cand else (None, None, 0)

        # Reported best across ALL candidates (informational: shows seasonal-naive
        # upside even where it is not shipped).
        report_best, report_best_mae, _ = _best(CHALLENGERS)
        # Shippable best: only among servable challengers (carry-forward today).
        best_ch, best_ch_mae, best_ch_n = _best(tuple(servable_challengers))

        switched = False
        delta_pct = None
        if (rec_mae is not None and best_ch_mae is not None and rec_mae > 0):
            delta_pct = (rec_mae - best_ch_mae) / rec_mae  # +ve => challenger better
            # No-regression vs the REAL serving incumbent (category-median tier):
            # a switch must also not be worse than what serving deploys today, so we
            # never trade a recency-mean strawman win for a real-world regression.
            beats_serving = (cat_mae is None) or (best_ch_mae <= cat_mae)
            if best_ch_n >= min_origins and delta_pct >= switch_margin and beats_serving:
                switched = True
        row.update(best_challenger=best_ch, report_best_challenger=report_best,
                   report_best_MAE=report_best_mae, delta_pct=delta_pct,
                   switched=switched)
        table.append(row)
        if switched:
            choice_map[str(cid).lower()] = best_ch

    aggregate = _aggregate_gate(long, choice_map, table)
    if not aggregate["applied"]:
        # Aggregate gate failed -> ship NO switches (null result is valid).
        for r in table:
            r["switched"] = False
        choice_map = {}
    return choice_map, table, aggregate


def _aggregate_gate(long: pd.DataFrame, choice_map: dict, table: list) -> dict:
    """Origin-weighted pooled MAE: all-incumbent vs switches-applied, over the
    fallback segment. Applied only if it does not regress AND no switched crop is
    worse than its own recency-mean baseline."""
    if not len(long):
        return {"applied": False, "reason": "no fallback walk-forward rows",
                "pooled_recmean_MAE": None, "pooled_switched_MAE": None,
                "n_rows": 0, "n_switched_crops": 0}
    y = long["y"].to_numpy(float)
    rec = long["recency_mean"].to_numpy(float)
    cat = long["category_median_serving"].to_numpy(float)   # REAL serving incumbent
    cids = long["CropId"].astype(str).str.lower().to_numpy()
    # switched-vs-recmean: challenger where the crop switched, else recmean.
    sw = rec.copy()
    # switched-vs-serving: challenger where switched, else the REAL served category
    # tier — the honest "does this beat what we deploy today" pooled comparison.
    sw_serv = cat.copy()
    for cid, ch in choice_map.items():
        m = cids == cid
        sw[m] = long[ch].to_numpy(float)[m]
        sw_serv[m] = long[ch].to_numpy(float)[m]
    def pooled(pred):
        mm = np.isfinite(y) & np.isfinite(pred)
        return (float(np.mean(np.abs(y[mm] - pred[mm]))), int(mm.sum())) if mm.any() else (None, 0)
    rec_mae, n_rec = pooled(rec)
    sw_mae, _ = pooled(sw)
    cat_mae, _ = pooled(cat)
    sw_serv_mae, _ = pooled(sw_serv)
    # per-crop no-regression guard (belt; the >=10% gate already implies it)
    no_regress = all(
        (r["recency_mean_MAE"] is None) or
        (r[f"{choice_map[r['cropId']]}_MAE"] <= r["recency_mean_MAE"])
        for r in table if r["cropId"] in choice_map)
    applied = bool(choice_map) and (sw_mae is not None and rec_mae is not None
                                    and sw_mae <= rec_mae and no_regress)
    reason = ("switches improve pooled fallback MAE and no switched crop regresses"
              if applied else
              ("no crop passed the per-crop switch gates" if not choice_map else
               "aggregate pooled MAE did not improve -> withhold all switches"))
    return {"applied": applied, "reason": reason,
            "pooled_recmean_MAE": rec_mae, "pooled_switched_MAE": sw_mae,
            # Honest real-world view: recency-mean is inflated by the cold-start
            # global-mean fallback for recent-onset crops, so ALSO report vs the
            # category-median tier serving actually deploys today.
            "pooled_serving_category_MAE": cat_mae,
            "pooled_switched_vs_serving_MAE": sw_serv_mae,
            "n_rows": n_rec, "n_switched_crops": len(choice_map),
            "no_regress": no_regress}
