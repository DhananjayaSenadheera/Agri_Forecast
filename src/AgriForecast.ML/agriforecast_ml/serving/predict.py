"""Harvest-price prediction logic.

Routes to the ML model when the gate promoted it; otherwise serves the
crop-mean fallback (current best predictor). Always returns an ordered
P10/P50/P90 interval and never crashes on unknown crops.
"""
from __future__ import annotations

import logging
from datetime import date, timedelta

import numpy as np
import pandas as pd
from sqlalchemy import text

from .. import features
from ..db import get_engine
from ..load import load_festivals, resolve_forecast_gp
from ..registry import registry
from . import explain
from .build_x import build_x
from .crop_categories import category_for

_log = logging.getLogger(__name__)

# R2 Step 5.3: serving reads agronomy from CropAgronomyProfiles and applies the
# IsVerified-STRICT exclusion predicate (unverified profile => no served horizon,
# crop-mean fallback). The transitional _warn_unverified_once() helper is retired
# — an unverified profile is now a hard skip, not a warned-through inclusion.


# Load the promoted payload once at import (serving is read-only).
_PAYLOAD, _META = registry.load_promoted()

# Cold-start default: used ONLY when the promoted payload predates this feature
# (i.e. no fallback["min_history_obs"] key). The live v10 payload lacks the key,
# so serving MUST degrade to this constant rather than crash — old-payload compat,
# mirroring the served_ml_kind default at _served_ml_kind(). See DECISIONS.md.
_DEFAULT_MIN_HISTORY_OBS = 365


def _min_history_obs() -> int:
    fb = (_PAYLOAD or {}).get("fallback") or {}
    v = fb.get("min_history_obs")
    return int(v) if v is not None else _DEFAULT_MIN_HISTORY_OBS


# --- v13 history gate: the ML-served crop set (lowercased GUID strings) --------
# The trainer fits the pooled model on long-history (>=min_history_obs labelled
# rows) crops ONLY and threads that set into the payload as `served_on_crops`.
# Serving routes any crop NOT in this set to the fallback ladder INTENTIONALLY
# (an explicit history-gate decision), not via the _model_quantiles_safe crash
# guard. Two consequences:
#   * a non-served crop never reaches the model, so it can never trigger the
#     unseen-category XGBoost raise -> the crash guard becomes a pure backstop
#     for genuinely unexpected mismatches (e.g. a live payload trained on a
#     different crop set), which is exactly what we want.
#   * OLD payloads (v11/v12) predate `served_on_crops`. When the key is ABSENT we
#     return None from _served_on_crops() and `_is_model_served` treats every crop
#     as eligible (legacy behavior: the crash guard then catches unseen crops).
def _served_on_crops() -> set[str] | None:
    v = (_PAYLOAD or {}).get("served_on_crops")
    if v is None:
        return None
    return {str(c).lower() for c in v}


def _is_model_served(crop_id: str) -> bool:
    """True iff this crop is inside the promoted model's history gate. Old payloads
    without `served_on_crops` treat every crop as eligible (legacy compat)."""
    served = _served_on_crops()
    if served is None:
        return True
    return str(crop_id).lower() in served


# --- Per-crop fallback-predictor choice (chip task_9b1cd894) ------------------
# The trainer backtests, per fallback-served crop, whether a challenger fallback
# (carry-forward = last observed AvgPrice) beats the recency-mean incumbent, and
# ships the winning switches in the (signed) payload under fallback["choice"] =
# {crop_id_lower: "carry_forward"}. Serving reads that map and re-centers the
# fallback interval on the chosen predictor. FAIL-CLOSED in every degenerate
# case (no key, old payload, unknown crop, missing/invalid price, any error) ->
# the crop keeps today's recency-mean/category fallback behavior. Only the
# central p50 + band CENTRE change; confidence stays "Low", every string field
# (confidenceReason/reasonCode/activePredictor/fallbackTier) is unchanged.
_SERVABLE_FALLBACK_CHOICES = {"carry_forward"}


def _fallback_choice(crop_id: str) -> str | None:
    """The shipped fallback-predictor choice for this crop, or None (= keep the
    recency-mean/category incumbent). Old payloads without the key -> None."""
    choice = ((_PAYLOAD or {}).get("fallback") or {}).get("choice") or {}
    ch = choice.get(str(crop_id).lower())
    return ch if ch in _SERVABLE_FALLBACK_CHOICES else None


# Max age of the carry-forward anchor. Mirrors the P3 macro convention
# (_MACRO_STALENESS_DAYS = 60 in features.py): a carried-forward value is only
# trusted for ~60 days. A switched crop that goes silent between retrains would
# otherwise anchor on an arbitrarily old price; past the cap we FAIL CLOSED to
# the incumbent (category/crop-median tier) rather than serve a stale level.
_CARRY_FORWARD_STALENESS_DAYS = 60


def _within_carry_forward_staleness(obs_date, as_of: date) -> bool:
    """True iff the anchor's observation is within the staleness cap of the
    request reference date. Pure/point-in-time (age measured against as_of, never
    a future date)."""
    age = (pd.Timestamp(as_of) - pd.Timestamp(obs_date)).days
    return age <= _CARRY_FORWARD_STALENESS_DAYS


def _carry_forward_price(crop_id: str, as_of: date) -> float | None:
    """Last NON-NULL positive AvgPrice at/before ``as_of`` (the carry-forward
    anchor), but ONLY if that observation is within the staleness cap. Returns
    None (-> caller fails closed to the incumbent) when there is no scoreable
    price OR the newest non-null price is older than the cap. BOTH endpoints use
    this so they agree on the last-non-null anchor + the 60d cap."""
    sql = text("""
        SELECT TOP 1 AvgPrice, ObservationDate FROM CropFeatureDaily
        WHERE CropId = :cid AND ObservationDate <= :asof AND AvgPrice IS NOT NULL
        ORDER BY ObservationDate DESC
    """)
    with get_engine().connect() as conn:
        df = pd.read_sql(sql, conn, params={"cid": crop_id, "asof": as_of})
    if not len(df) or pd.isna(df["AvgPrice"].iloc[0]):
        return None
    if not _within_carry_forward_staleness(df["ObservationDate"].iloc[0], as_of):
        return None
    v = float(df["AvgPrice"].iloc[0])
    return v if v > 0 else None


def _spread_source(crop_id: str, fb_q: dict) -> dict:
    """The band half-spreads to keep when re-centring on a switched predictor.
    Prefer the crop's OWN per-crop quantiles (its true price dispersion) over the
    resolved tier's (which, at the category tier, pools many crops and is far too
    wide for a narrow-level crop). Falls back to the resolved tier if the crop has
    no own per-crop entry (old payloads / unknown crop)."""
    per = ((_PAYLOAD or {}).get("fallback") or {}).get("per_crop", {}).get(crop_id)
    return per if per is not None else fb_q


def _recenter_on(spread_q: dict, centre: float) -> tuple[float, float, float]:
    """Re-centre the fallback interval on ``centre`` (the chosen point predictor),
    keeping ``spread_q``'s asymmetric half-spreads. The band is DELIBERATELY
    absolute-width — the crop's historical Rs half-spreads (p50-p10, p90-p50), NOT
    a proportion of the new anchor level — so a low current price still carries a
    realistic Rs uncertainty band rather than an artificially tight one; and
    confidence stays pinned "Low" at the call sites regardless. Lower bound is
    clamped >=0 (price is non-negative); ordering is left to the caller's sort."""
    lower_gap = max(float(spread_q["p50"]) - float(spread_q["p10"]), 0.0)
    upper_gap = max(float(spread_q["p90"]) - float(spread_q["p50"]), 0.0)
    return max(centre - lower_gap, 0.0), centre, centre + upper_gap


def _resolve_fallback(crop_id: str, crop_name: str | None = None) -> tuple[dict, str]:
    """Fallback ladder: per-crop (if n_obs >= threshold) -> category -> global.

    Returns (quantile_dict, tier) where tier in {"crop", "category", "global"}.
    Fully old-payload compatible: a per-crop entry without an "n_obs" key (pre-P4
    payloads) is treated as adequate history (tier "crop") — we never fabricate a
    cold-start flag the trained-on data can't support, preserving prior behavior.
    """
    fb = _PAYLOAD["fallback"]
    per = fb["per_crop"].get(crop_id)
    if per is not None:
        n_obs = per.get("n_obs")
        # Missing n_obs -> old payload: keep legacy behavior (trust per-crop).
        if n_obs is None or int(n_obs) >= _min_history_obs():
            return per, "crop"
    # Per-crop absent or thin: try the category rung (P4 addition; absent in old payloads).
    cat = category_for(crop_id, crop_name)
    by_cat = fb.get("by_category") or {}
    if cat and cat in by_cat:
        return by_cat[cat], "category"
    return fb["global"], "global"


# --- Confidence rungs (PROPOSED — owner sign-off at review) ------------------
# Preserves the EXACT spelling + .NET MlContract.cs semantics of "Low"/"Medium":
#   * "Low"    -> caps the .NET recommendation (fail-closed, unchanged trigger).
#   * "Medium" -> unchanged.
#   * "High"   -> NEW value. Safe under the .NET client (case-insensitive,
#                 unmapped-ignore) AND the recommendation matrix: the ceiling logic
#                 only LOWERS trust on lowTrust=(fallback|Low); "High" is never
#                 compared, so it enables StronglyRecommended where "Medium" already
#                 did NOT cap. It does NOT flip any existing branch to a WORSE
#                 outcome. Emitted ONLY when the ML model is served AND the crop has
#                 adequate own history (tier == "crop").
def _confidence_for(model_served: bool, tier: str, row_missing: bool = False,
                    ml_failed: bool = False, not_model_served: bool = False) -> str:
    # row_missing: the fallback path was taken because NO scoreable CropFeatureDaily
    # row exists as of the plant date. The prediction is inert-until-retrain, so it
    # must NOT inherit the resolved tier's (possibly Medium/High) trust — clamp to
    # "Low" regardless of tier. Restores the pre-step-1 downgrade the fallback
    # refactor silently dropped on the live v10 payload. Exact "Low" spelling is
    # load-bearing (.NET MlContract is fail-closed on it).
    # ml_failed: the ML path was ATTEMPTED (promoted model, scoreable row) but the
    # predict call itself failed (_model_quantiles_safe -> None, e.g. a CropId the
    # incumbent encoder never saw after a corpus widening). Equally inert-until-
    # retrain — same clamp, so a trained-crop tier can never report Medium/High for
    # a forecast the model refused to score.
    # not_model_served: the v13 history gate routed this crop to the baseline.
    # Today the gate threshold equals the fallback "crop"-tier threshold (both
    # _DEFAULT_MIN_HISTORY_OBS), so tier already resolves at most category/global
    # -> Low; this explicit clamp removes that coupling so a future divergence of
    # the two thresholds can never let a gated-out crop report Medium/High.
    if row_missing or ml_failed or not_model_served:
        return "Low"
    if tier == "global":
        return "Low"          # unknown / no category prior — genuine cold start
    if model_served and tier == "crop":
        return "High"
    if tier == "category":
        return "Low"          # borrowed prior, thin own history -> honest low trust
    return "Medium"           # adequate own history, fallback or ML


def _reason_for(model_served: bool, tier: str, row_missing: bool = False,
                ml_failed: bool = False, not_model_served: bool = False) -> str:
    """Human-readable justification for the confidence rung (additive field)."""
    if not_model_served:
        # v13 history gate: this crop has too little history for the ML model to
        # be trained/served on it, so it is INTENTIONALLY routed to the baseline
        # (distinct from ml_failed, which is an unexpected predict-time failure).
        return ("This crop does not yet have enough price history for the ML "
                "model; serving a baseline prior until it accumulates more.")
    if ml_failed:
        return ("The current model cannot score this crop yet (its data arrived "
                "after the model was trained); serving a fallback prior with low "
                "confidence until the next retrain.")
    if row_missing:
        return ("No recent feature row is available for this crop as of the plant "
                "date; serving a fallback prior with low confidence.")
    if tier == "global":
        return ("No history for this crop or its category; using an overall "
                "price prior (cold start).")
    if tier == "category":
        return ("Too little history for this crop; using a similar-crop category "
                "prior (cold start).")
    if model_served and tier == "crop":
        return "ML model served with adequate crop history."
    return "Adequate crop history."


# --- API-5: machine-stable reason CODES (additive; prose fields unchanged) ---
# Every distinct confidenceReason prose branch in `_reason_for` gets ONE stable
# snake_case code + a params dict, so the trilingual FE renders Sinhala/Tamil
# without parsing English. Branch order MUST mirror `_reason_for` exactly (one
# code per distinct prose branch — no invented branches). The FE renders the
# `explanation` prose from the already-stable `activePredictor` + `fallbackTier`
# fields, so only the confidenceReason branch needs a code here.
def _reason_code_for(model_served: bool, tier: str, row_missing: bool = False,
                     ml_failed: bool = False, not_model_served: bool = False,
                     history_obs: int | None = None) -> tuple[str, dict]:
    # First-match wins: correctness relies on CALLERS keeping the three flags
    # mutually exclusive (they do — see predict_harvest/timeline call sites).
    # A caller that ever sets two flags together would silently take the first
    # branch, matching _reason_for's identical ordering.
    if not_model_served:
        params: dict = {"neededHistoryObs": _min_history_obs()}
        if history_obs is not None:
            params["historyObs"] = int(history_obs)
        return "not_model_served", params
    if ml_failed:
        return "ml_failed_fallback", {}
    if row_missing:
        return "no_recent_feature_row", {}
    if tier == "global":
        return "cold_start_global", {}
    if tier == "category":
        return "cold_start_category", {}
    if model_served and tier == "crop":
        return "model_served", {}
    return "adequate_history_fallback", {}


def _crop_history_obs(crop_id: str) -> int | None:
    """The crop's own labelled-row count from the persisted fallback map (used as
    a reason param). None when the payload has no per-crop n_obs (old payloads)."""
    per = ((_PAYLOAD or {}).get("fallback") or {}).get("per_crop", {}).get(crop_id) or {}
    n = per.get("n_obs")
    return int(n) if n is not None else None


def _fallback_explanation(tier: str) -> str:
    base = ("The ML model is not yet more accurate than this baseline at current "
            "data volume.")
    if tier == "category":
        return ("Based on a similar-crop category's historical harvest-price "
                f"distribution (too little history for this crop). ({base})")
    if tier == "global":
        return ("Based on the overall historical harvest-price distribution "
                f"(no data for this crop or its category). ({base})")
    return ("Based on this crop's historical harvest-price distribution. "
            f"({base})")


def _latest_feature_row(crop_id: str, plant_date: date):
    sql = text("""
        SELECT TOP 1 * FROM CropFeatureDaily
        WHERE CropId = :cid AND ObservationDate <= :pdate
        ORDER BY ObservationDate DESC
    """)
    with get_engine().connect() as conn:
        df = pd.read_sql(sql, conn, params={"cid": crop_id, "pdate": plant_date})
    return df.iloc[0] if len(df) else None


def _crop_meta(crop_id: str):
    """Resolve (crop_name, growth_period_days) for the serving fallback path.

    R2 Step 2.3 CUT-OVER: GrowthPeriodDays now comes from ``CropAgronomyProfiles``
    (the source of truth), NOT ``Crops.GrowthPeriodDays`` (dropped in 2.4). LEFT
    JOIN so a crop without a profile still returns its name with gp=None (the
    caller then serves the crop-mean fallback with no harvest horizon).

    R2 Step 5.3 exclusion predicate (IsVerified-STRICT): a crop resolves a served
    harvest horizon ONLY IF its profile is owner-verified (``IsVerified == 1``)
    AND has a usable GrowthPeriodDays. An unverified profile — even one holding a
    legacy GrowthPeriodDays — returns gp=None here, so the caller degrades to the
    crop-mean fallback with NO harvest horizon (identical to a NULL-gp or
    profile-less crop). Routes through the shared ``load.resolve_forecast_gp`` so
    serving applies the SAME gate the feature build uses (no train/serve skew).
    """
    sql = text("""
        SELECT c.Name, p.GrowthPeriodDays, p.IsVerified
        FROM Crops c
        LEFT JOIN CropAgronomyProfiles p ON p.CropId = c.Id
        WHERE c.Id = :cid
    """)
    with get_engine().connect() as conn:
        df = pd.read_sql(sql, conn, params={"cid": crop_id})
    if not len(df):
        return None, None
    gp = resolve_forecast_gp(df["IsVerified"].iloc[0], df["GrowthPeriodDays"].iloc[0])
    return df["Name"].iloc[0], gp


# ML kinds whose serving artifacts this module knows how to honor. A promoted
# version advertising any other served_ml_kind must NOT be served as an ML model
# (its artifacts are not persisted/understood here) -> fail loud, fall back safe.
_SERVABLE_ML_KINDS = {"model", "residual"}


def _served_ml_kind() -> str:
    # Older payloads predate served_ml_kind; default to the pooled "model" path.
    return str((_PAYLOAD or {}).get("served_ml_kind", "model"))


def _ml_servable() -> bool:
    """True iff the promoted ML path is one we can actually serve with persisted
    artifacts. Guards the long-flagged trap: serving must never honor a
    non-`model` ML kind whose artifacts aren't in the registry."""
    kind = _served_ml_kind()
    if kind not in _SERVABLE_ML_KINDS:
        _log.error(
            "Promoted version %s advertises served_ml_kind=%r which serving "
            "cannot honor (artifacts not persisted/understood) -> serving the "
            "crop-mean fallback instead. Persist this kind's artifacts and add "
            "a serving path before promoting it.",
            (_META or {}).get("version"), kind)
        return False
    if kind == "residual":
        ok = ("residual_models" in _PAYLOAD
              and "residual_offsets" in _PAYLOAD
              and "residual_offset_global" in _PAYLOAD)
        if not ok:
            _log.error(
                "Promoted version %s served_ml_kind='residual' but residual "
                "artifacts are missing from the payload -> serving crop-mean "
                "fallback. Retrain so residual_models/residual_offsets are "
                "persisted.", (_META or {}).get("version"))
        return ok
    return "models" in _PAYLOAD


def _build_X(row):
    # Single source of truth for the serve-time input frame lives in
    # serving/build_x.py (shared with explain.py so coercion / NaN discipline
    # can never drift between the predict and SHAP paths). See that module for
    # the deliberate NaN-never-0 serving contract.
    return build_x(row, _PAYLOAD)


def _residual_offset(crop_id: str) -> float:
    """Per-crop residual offset, looked up from the PERSISTED train-only map.
    Point-in-time / leakage-safe: the map was fixed at training time from data
    at/before train; serving never recomputes it from current/future data."""
    offsets = _PAYLOAD["residual_offsets"]
    return float(offsets.get(str(crop_id).lower(), _PAYLOAD["residual_offset_global"]))


def _model_quantiles_safe(row, crop_id: str | None = None) -> dict | None:
    """Wrap _model_quantiles so an unscoreable row degrades to the fallback ladder
    instead of 500-ing the request.

    Point-in-time note: this is a SERVING-ROBUSTNESS guard, not a model change.
    The pooled model's CropId is a categorical feature, so XGBoost's categorical
    encoder RAISES on a CropId it never saw in training (`Found a category not in
    the training set`). This bites whenever the promoted model was trained on a
    SMALLER crop set than the feature store now covers — e.g. after a corpus
    widening that unlocks new forecastable crops (R2 Step 6/7: Beans, Snake Gourd,
    +18 HARTI crops) while an OLDER model (v11, 11 crops) is still promoted. Those
    crops now HAVE a feature row (so they reach the ML path) but are unknown to the
    incumbent encoder. Rather than crash, degrade to the crop-mean fallback ladder,
    exactly as if there were no scoreable row. Also catches any other predict-time
    artifact mismatch. Returns None to signal "serve the fallback instead".
    """
    try:
        return _model_quantiles(row, crop_id)
    except Exception:
        _log.warning(
            "ML quantile prediction failed for crop %s under promoted model %s "
            "(likely a CropId unseen by the incumbent encoder after a corpus "
            "widening) -> serving crop-mean fallback.",
            crop_id, (_META or {}).get("version"),
            exc_info=True)  # server log only; the API layer never returns tracebacks
        return None


def _model_quantiles_frame(X, crop_id: str | None = None) -> dict:
    """Quantile predictions for an ALREADY-BUILT model-input frame of 1..N rows.

    served_ml_kind == "model":    expm1(model_pred)
    served_ml_kind == "residual": expm1(model_pred + log1p(offset)) where offset
                                  is the persisted per-crop train-only mean.

    The inverse-transform maths lives here ONCE so the single-shot path
    (_model_quantiles) and the multi-date what-if sweep (harvest_window) can never
    disagree about how a raw model output becomes a rupee price. Returns arrays
    aligned with X's rows.
    """
    kind = _served_ml_kind()
    if kind == "residual":
        log_off = float(np.log1p(_residual_offset(crop_id)))
        return {q: np.expm1(mdl.predict(X) + log_off)
                for q, mdl in _PAYLOAD["residual_models"].items()}
    # default pooled "model" path
    return {q: np.expm1(mdl.predict(X)) for q, mdl in _PAYLOAD["models"].items()}


def _model_quantiles(row, crop_id: str | None = None) -> dict:
    """Quantile predictions for ONE feature row (the 1-row case of the frame path)."""
    out = _model_quantiles_frame(_build_X(row), crop_id)
    return {q: float(v[0]) for q, v in out.items()}


# =============================================================================
# The WHAT-IF row — the ONE definition of "score this crop for planting date D"
# =============================================================================
# Both date-answering endpoints ask the same question — /predict asks it once,
# /harvest-window asks it for every candidate date — so they must build the model
# input the same way. They did not: harvest_window built a what-if row per date
# while predict_harvest scored the anchor row untouched. Result (the reported
# symptom): the window panel recommended a planting date, the forecast screen
# quoted a different price for that SAME date, and a farmer who picked a date from
# INSIDE the recommended window could then be told "not recommended". That
# contradiction is the bug this helper exists to make structurally impossible.
#
# The mechanism. _latest_feature_row(crop, D) returns the newest row with
# ObservationDate <= D. For any FUTURE D that is TODAY's row; even for a PAST D it
# is usually some days earlier than D. Scoring it unchanged answers "what if I
# plant on the anchor's observation date", never "what if I plant on D".
#
# So we build a WHAT-IF row per requested date:
#   FROZEN at the anchor row's values -- price/lags/rolling means, weather, macro,
#     FX, sentiment, policy, cross-market spreads. These are genuinely unknown for
#     a future date; holding them at their last OBSERVED value is the honest
#     choice (assuming anything else would be inventing data).
#   RECOMPUTED for the requested date -- the calendar/seasonality columns and the
#     harvest-anchored festival columns, through features.calendar_features /
#     features._festival_features, i.e. the SAME code the training rows were built
#     with (no train/serve skew one layer above what build_x.py guards).
#
# Why the recompute is NOT lookahead. Those columns are pure functions of a DATE
# plus a festival calendar that is gazetted in advance. Knowing that day-of-year
# 104 sits where it sits in the seasonal cycle, or that 2026-04-13 is Avurudu,
# requires zero knowledge of the future market. Nothing that would have to be
# OBSERVED (a price, a rainfall total, a CPI print) is ever moved forward — that
# is the line, and it is exactly the line the feature build draws.
#
# Past dates get the recompute too: encoding the date the farmer actually asked
# about is strictly more correct than inheriting the anchor's older encoding, and
# it is the same deterministic calendar maths either way.
# --- Festival-calendar cache -------------------------------------------------
# _whatif_rows now runs on EVERY /predict and /timeline request, not just the
# sweep, so re-reading FestivalCalendarEntries per request would put a DB
# round-trip on the two hottest endpoints for a ~64-row table that changes a few
# times a year. Cached process-wide, exactly like the promoted _PAYLOAD above.
#
# STALENESS TRADEOFF (write this down rather than discover it later): a gazette
# update — a new/moved festival date seeded through the .NET admin path — does
# NOT reach a running serving process until it restarts. That is the same
# lifecycle the promoted model payload already has (loaded once at import), so
# serving is consistent about it: model + calendar both refresh on deploy.
#
# Deliberately NOT cached: an EMPTY calendar. load_festivals() swallows DB errors
# and returns an empty frame, so "empty" is indistinguishable from "unreachable";
# caching it would pin a transient outage into festival-blind forecasts until the
# next restart. Empty is cheap to re-query, so we retry on the next request.
# Tests force a reload by monkeypatching _FESTIVAL_WINDOWS back to None.
_FESTIVAL_WINDOWS: tuple | None = None


def _festival_windows_cached():
    """(events_arr, leadup_windows) for the what-if build — see the cache note."""
    global _FESTIVAL_WINDOWS
    if _FESTIVAL_WINDOWS is not None:
        return _FESTIVAL_WINDOWS
    try:
        events_arr, windows = features._festival_windows(load_festivals())
    except Exception:
        # A missing/unreadable calendar must not fail the request; degrade to "no
        # festivals known", exactly as the feature build does. Seasonality still
        # varies by date, so the answer stays meaningful (just festival-blind).
        # Not cached -> the next request retries.
        _log.warning("Festival calendar unavailable for the what-if row build "
                     "— seasonality only.", exc_info=True)
        return features._festival_windows(None)
    if events_arr.size:
        _FESTIVAL_WINDOWS = (events_arr, windows)
    return events_arr, windows


def _whatif_rows(anchor_row, plant_dates, gp: int) -> list[dict]:
    """One what-if row (plain dict, ready for build_x) per planting date.

    ``anchor_row`` is a CropFeatureDaily row (pandas Series or dict) whose
    non-calendar columns are frozen; ``gp`` is the crop's growth period in days
    and anchors the festival features on the HARVEST date, matching how the
    training label (price.shift(-GrowthPeriodDays)) was constructed.

    Raises only on a genuinely broken calendar/date computation; every caller
    treats that as "cannot honestly encode this date" and degrades to its own
    fallback rather than scoring the anchor's stale calendar.
    """
    plant_idx = pd.DatetimeIndex(list(plant_dates))
    harvest_idx = plant_idx + pd.Timedelta(days=int(gp))

    cal = features.calendar_features(plant_idx)
    events_arr, fest_windows = _festival_windows_cached()
    fest = features._festival_features(
        pd.Series(plant_idx), pd.Series(harvest_idx), events_arr, fest_windows)

    anchor = dict(anchor_row)
    rows: list[dict] = []
    for i in range(len(plant_idx)):
        whatif = dict(anchor)  # frozen: price / weather / macro / market columns
        for c in features.CALENDAR_FEATURE_COLS:
            whatif[c] = cal.iloc[i][c]
        for c in features.FESTIVAL_FEATURE_COLS:
            whatif[c] = fest.iloc[i][c]
        rows.append(whatif)
    return rows


def _ordered_interval(p10, p50, p90) -> tuple[float, float, float]:
    """Round to 2dp and force p10 <= p50 <= p90.

    ONE rounding rule for both date-answering endpoints, so /predict for date D
    and the /harvest-window point for date D can never print different numbers.
    The quantile models are fitted independently and CAN cross on a given row;
    sorting is the tie-break. It is display-only — harvest_window still ranks on
    the RAW p50s, so this can never move the recommended window.
    """
    lo, mid, hi = sorted([round(float(p10), 2), round(float(p50), 2),
                          round(float(p90), 2)])
    return lo, mid, hi


def predict_harvest(crop_id: str, plant_date: date) -> dict:
    if _PAYLOAD is None:
        raise RuntimeError("No model registered — run train_model_a.py first.")

    crop_id = str(crop_id).lower()  # GUID case varies by caller; normalize for lookups
    row = _latest_feature_row(crop_id, plant_date)
    crop_name, gp = (row["CropName"], int(row["GrowthPeriodDays"])) if row is not None \
        and pd.notna(row.get("GrowthPeriodDays")) else _crop_meta(crop_id)

    harvest_date = plant_date + timedelta(days=gp) if gp else None
    # Serve the ML path only when the gate promoted it AND its artifacts are
    # present/understood here (guards the residual/blend latent trap).
    model_active = bool(_PAYLOAD.get("beats_baseline")) and _ml_servable()

    # top_factors is emitted ONLY on the model-served path; the fallback path
    # OMITS the field entirely (the FE shows an honest "no breakdown" note).
    top_factors: list[dict] | None = None
    # Resolve the fallback rung once (used for confidence even when ML serves, so
    # a thin-history ML-covered crop is not over-trusted).
    fb_q, tier = _resolve_fallback(crop_id, crop_name)
    # v13 history gate: the ML path is ATTEMPTED only for crops in the promoted
    # model's served set (intentional routing). A non-served crop skips the model
    # entirely and takes the fallback ladder with a distinct, non-ml_failed reason
    # — so its fallback is a deliberate decision, not an exception the crash guard
    # happened to catch.
    model_served_crop = _is_model_served(crop_id)
    ml_attempted = bool(model_active and model_served_crop and row is not None and gp)
    # Score the WHAT-IF row for the REQUESTED plant date, never the anchor row as
    # it happened to be observed (see the what-if section above). Without this the
    # anchor's own — for a future date, TODAY's — calendar encoding is scored, so
    # every plant date returns the same price and /predict contradicts
    # /harvest-window, which has always recomputed the date columns. The fallback
    # path below scores no model row at all and is therefore untouched by this.
    score_row = None
    if ml_attempted:
        try:
            score_row = _whatif_rows(row, [plant_date], int(gp))[0]
        except Exception:
            # We cannot honestly encode the requested date -> do NOT quietly score
            # the anchor's stale calendar instead. Degrade through the same
            # ml_failed clamp as any other predict-time failure (Low confidence).
            _log.warning(
                "What-if row construction failed for crop %s / plant date %s "
                "-> serving the fallback ladder.", crop_id, plant_date,
                exc_info=True)  # server log only; the API never returns tracebacks
    q = _model_quantiles_safe(score_row, crop_id) if score_row is not None else None
    if q is not None:
        p10, p50, p90 = q["p10"], q["p50"], q["p90"]
        active = _served_ml_kind()
        confidence = _confidence_for(model_served=True, tier=tier)
        confidence_reason = _reason_for(model_served=True, tier=tier)
        reason_code, reason_params = _reason_code_for(model_served=True, tier=tier)
        explanation = "ML model forecast from current price, season and recent weather."
        # Farmer-meaningful factor codes from the ACTUAL promoted model (SHAP on
        # the served p50 model), aggregated per code. Deterministic per input.
        # MUST be the same what-if row the quantiles came from — SHAP over the raw
        # anchor row would explain a DIFFERENT input than the number printed above
        # it (e.g. crediting January seasonality for an August planting).
        top_factors = explain.top_factor_codes(score_row, _PAYLOAD, top_n=4)
    else:
        p10, p50, p90 = fb_q["p10"], fb_q["p50"], fb_q["p90"]
        active = "crop_mean_fallback"
        # Per-crop fallback-predictor switch (chip task_9b1cd894): if the payload
        # selected carry-forward for this crop, re-centre the interval on the last
        # NON-NULL AvgPrice within the staleness cap. Uses _carry_forward_price
        # (NOT row["AvgPrice"], whose newest row may be NULL) so predict_harvest
        # and timeline agree on the anchor + 60d cap. Purely a better central
        # estimate; all string/confidence fields stay unchanged. Fail-closed: no
        # scoreable / too-stale price keeps the incumbent quantiles.
        if _fallback_choice(crop_id) == "carry_forward":
            cf = _carry_forward_price(crop_id, plant_date)
            if cf is not None:
                p10, p50, p90 = _recenter_on(_spread_source(crop_id, fb_q), cf)
        # When the fallback is taken because there is NO scoreable feature row as of
        # the plant date, the prediction is un-scoreable/inert -> force "Low" trust
        # regardless of the resolved tier (fallbackTier still reports the real tier).
        # Same clamp when the ML path was attempted but the predict call failed
        # (_model_quantiles_safe -> None, e.g. incumbent encoder predates this crop).
        row_missing = row is None
        ml_failed = ml_attempted and q is None
        # not_model_served: the model is live+servable and a feature row exists,
        # but this crop is outside the history gate -> deliberate baseline route
        # (distinct reason from an unexpected ml_failed). Only meaningful when the
        # row/gp exist (otherwise row_missing already explains the fallback).
        not_model_served = bool(model_active and not model_served_crop
                                and row is not None and gp)
        confidence = _confidence_for(model_served=False, tier=tier,
                                     row_missing=row_missing, ml_failed=ml_failed,
                                     not_model_served=not_model_served)
        confidence_reason = _reason_for(model_served=False, tier=tier,
                                        row_missing=row_missing, ml_failed=ml_failed,
                                        not_model_served=not_model_served)
        reason_code, reason_params = _reason_code_for(
            model_served=False, tier=tier, row_missing=row_missing,
            ml_failed=ml_failed, not_model_served=not_model_served,
            history_obs=_crop_history_obs(crop_id))
        explanation = _fallback_explanation(tier)

    p10, p50, p90 = _ordered_interval(p10, p50, p90)
    result = {
        "cropId": crop_id,
        "cropName": crop_name,
        "plantDate": plant_date.isoformat(),
        "harvestDate": harvest_date.isoformat() if harvest_date else None,
        "growthPeriodDays": gp,
        "predictedPrice": p50,
        "lowerBound": p10,
        "upperBound": p90,
        "confidence": confidence,
        "confidenceReason": confidence_reason,
        "reasonCode": reason_code,
        "reasonParams": reason_params,
        "fallbackTier": tier,
        "activePredictor": active,
        "modelVersion": (_META or {}).get("version"),
        "explanation": explanation,
    }
    # topFactors is present ONLY on the model-served path (fallback omits it).
    if top_factors is not None:
        result["topFactors"] = top_factors
    return result


# =============================================================================
# Best harvest window — the "when should I plant to sell into a good price?" sweep
# =============================================================================
# HOW IT WORKS, and why it is honest.
#
# The model is trained as: features known at observation date D -> price at
# D + GrowthPeriodDays. So scoring a row IS asking "if I plant on D, what will the
# harvest fetch?". Sweeping D over the next few months and comparing the answers
# is therefore a question the model was literally built to answer.
#
# The trap: _latest_feature_row(crop, D) returns the newest row with
# ObservationDate <= D, so for every FUTURE D that is the same row — today's. A
# naive sweep re-scores identical inputs and draws a perfectly flat line, then
# "picks a winner" out of floating-point noise. That is the failure mode this
# function exists to avoid.
#
# The fix is a WHAT-IF row per candidate date, built by the SHARED _whatif_rows()
# above (freeze the observed columns, recompute the calendar + harvest-anchored
# festival columns) — see that section for the full freeze/recompute rule and the
# leakage reasoning. It is shared with predict_harvest precisely so that the point
# this sweep shows for date D and the forecast /predict returns for date D are the
# same number; two implementations of the rule would drift back into contradicting
# each other.
#
# What the farmer is therefore being told: "holding today's market and weather
# conditions constant, this is how the SEASONAL and FESTIVAL-DEMAND structure of
# the year prices your harvest." It ranks TIMING, not future weather — and the
# API says exactly that in `explanation` so the UI cannot overclaim.
#
# Every path that cannot support that claim returns rankable=False with a reason
# code instead of a window. Fabricating a recommendation here would be worse than
# offering none: a farmer can plant on a date they cannot un-plant.

_WINDOW_HORIZON_DAYS = 90     # default sweep length (candidate planting dates)
_WINDOW_MAX_HORIZON = 365     # hard cap: one seasonal cycle; beyond it the frozen
                              # price anchor is far too stale to mean anything
_WINDOW_LEN_DEFAULT = 14      # window length when the crop has no HarvestWindowDays
_WINDOW_LEN_MIN = 7           # a 1-3 day "window" is not actionable advice
_WINDOW_LEN_MAX = 30
# Peak-to-trough spread, as a fraction of the median forecast, below which we
# refuse to name a best window. A curve this flat carries no timing signal — any
# "winner" would be model noise dressed up as advice.
_WINDOW_FLAT_EPS = 0.005


def _window_len_for(row) -> int:
    """Window length in days: the crop's own HarvestWindowDays (how long it keeps
    yielding once mature) when curated, else a sane default. Clamped so the answer
    is always actionable — neither a single day nor a whole season."""
    raw = row.get("HarvestWindowDays") if row is not None else None
    n = int(raw) if raw is not None and pd.notna(raw) else _WINDOW_LEN_DEFAULT
    return max(_WINDOW_LEN_MIN, min(n, _WINDOW_LEN_MAX))


def _window_unavailable(crop_id, crop_name, as_of, gp, reason_code, explanation):
    """The honest empty answer: no points, no window, and a code saying why."""
    return {
        "cropId": crop_id,
        "cropName": crop_name,
        "asOf": as_of.isoformat(),
        "growthPeriodDays": gp,
        "rankable": False,
        "reasonCode": reason_code,
        "activePredictor": "unavailable",
        "confidence": "Low",
        "modelVersion": (_META or {}).get("version"),
        "explanation": explanation,
        "windowDays": None,
        "points": [],
        "best": None,
    }


def harvest_window(crop_id: str, as_of: date,
                   horizon_days: int = _WINDOW_HORIZON_DAYS) -> dict:
    """Rank candidate planting dates over the next `horizon_days` by the price
    their harvest is forecast to fetch, and name the best contiguous window.

    See the module-section comment above for the what-if construction and the
    honesty gates. Returns rankable=False (never a fabricated window) whenever the
    crop has no harvest horizon, the promoted model cannot serve it, or the
    resulting curve is too flat to carry a timing signal.
    """
    if _PAYLOAD is None:
        raise RuntimeError("No model registered — run train_model_a.py first.")

    crop_id = str(crop_id).lower()  # GUID case varies by caller; normalize
    horizon_days = max(1, min(int(horizon_days), _WINDOW_MAX_HORIZON))

    row = _latest_feature_row(crop_id, as_of)
    crop_name, gp = (row["CropName"], int(row["GrowthPeriodDays"])) if row is not None \
        and pd.notna(row.get("GrowthPeriodDays")) else _crop_meta(crop_id)

    # --- honesty gates, in the order that makes the reason most specific --------
    if not gp:
        return _window_unavailable(
            crop_id, crop_name, as_of, None, "no_growth_period",
            "This crop has no verified growth period yet, so we cannot say which "
            "planting date leads to which harvest date.")
    if row is None:
        return _window_unavailable(
            crop_id, crop_name, as_of, gp, "no_feature_row",
            "We have no recent data for this crop to base a timing comparison on.")
    if not (bool(_PAYLOAD.get("beats_baseline")) and _ml_servable()):
        return _window_unavailable(
            crop_id, crop_name, as_of, gp, "model_inactive",
            "The forecasting model is not active, and the baseline predictor is "
            "the same for every date — so it cannot rank planting dates.")
    if not _is_model_served(crop_id):
        return _window_unavailable(
            crop_id, crop_name, as_of, gp, "crop_not_model_served",
            "We are still collecting data for this crop. Until the model covers "
            "it, every date would return the same price — so we will not guess.")

    # --- build the what-if rows (the SHARED construction, one per date) ----------
    plant_dates = [as_of + timedelta(days=i) for i in range(horizon_days + 1)]
    harvest_dates = [d + timedelta(days=gp) for d in plant_dates]

    # The row BUILD sits inside the same guard as the scoring call on purpose: a
    # failure to encode the candidate dates (calendar_features / _festival_features
    # raising) is the same class of event as a failure to score them — we cannot
    # honestly compare planting dates either way. Keeping it outside would make
    # this the ONE endpoint that 500s where /predict and /timeline degrade, for the
    # identical underlying fault.
    try:
        # Per-row build_x so the coercion + NaN discipline is byte-identical to the
        # single-shot path; batched below purely to predict once, not N times.
        frames = [build_x(whatif, _PAYLOAD)
                  for whatif in _whatif_rows(row, plant_dates, gp)]
        X = pd.concat(frames, ignore_index=True)
        for c in _PAYLOAD["categorical"]:
            if c in X.columns:
                X[c] = X[c].astype("category")  # concat can drop the category dtype
        q = _model_quantiles_frame(X, crop_id)
    except Exception:
        _log.warning("Harvest-window sweep failed to build or score the what-if "
                     "rows for crop %s under promoted model %s.",
                     crop_id, (_META or {}).get("version"), exc_info=True)
        return _window_unavailable(
            crop_id, crop_name, as_of, gp, "scoring_failed",
            "We could not compare planting dates for this crop just now.")

    p10s, p50s, p90s = q["p10"], q["p50"], q["p90"]

    # --- flat-curve gate: refuse to rank noise ----------------------------------
    level = float(np.median(p50s))
    spread = float(np.max(p50s) - np.min(p50s))
    if level <= 0 or (spread / level) < _WINDOW_FLAT_EPS:
        return _window_unavailable(
            crop_id, crop_name, as_of, gp, "flat_curve",
            "For this crop the forecast is effectively the same whenever you "
            "plant, so there is no better or worse time to aim for.")

    # --- pick the best contiguous window ---------------------------------------
    window_days = _window_len_for(row)
    span = min(window_days, len(p50s))
    means = np.convolve(p50s, np.ones(span) / span, mode="valid")
    start = int(np.argmax(means))
    end = start + span - 1

    # Uplift is stated against the AVERAGE date in the swept horizon — "better than
    # planting at a typical time", not against the worst date (which would inflate
    # the number) nor against today (which would flip sign arbitrarily).
    baseline = float(np.mean(p50s))
    best_price = float(means[start])

    # Rounded through the SAME _ordered_interval as predict_harvest, so a point
    # here and /predict for that point's date print identical numbers.
    points = []
    for i in range(len(plant_dates)):
        lo, mid, hi = _ordered_interval(p10s[i], p50s[i], p90s[i])
        points.append({
            "plantDate": plant_dates[i].isoformat(),
            "harvestDate": harvest_dates[i].isoformat(),
            "predictedPrice": mid,
            "lowerBound": lo,
            "upperBound": hi,
            "inBestWindow": start <= i <= end,
        })

    # The best block's band is the window-average of the p10s/p90s. Rounded through
    # _ordered_interval like everything else so lowerBound <= predictedPrice <=
    # upperBound holds here too: averaging cannot fix crossed quantiles, and this
    # block is the one part of the response nothing renders today, which is exactly
    # how an ordering violation would ship unnoticed.
    best_lo, best_mid, best_hi = _ordered_interval(
        float(np.mean(p10s[start:end + 1])), best_price,
        float(np.mean(p90s[start:end + 1])))

    _, tier = _resolve_fallback(crop_id, crop_name)
    return {
        "cropId": crop_id,
        "cropName": crop_name,
        "asOf": as_of.isoformat(),
        "growthPeriodDays": gp,
        "rankable": True,
        "reasonCode": "ml_served",
        "activePredictor": _served_ml_kind(),
        "confidence": _confidence_for(model_served=True, tier=tier),
        "modelVersion": (_META or {}).get("version"),
        "explanation": (
            "Compares planting dates using the season and festival demand around "
            "each harvest date. Today's prices and weather are held constant, so "
            "this ranks TIMING — it is not a weather forecast."),
        "windowDays": span,
        "points": points,
        "best": {
            "plantStart": plant_dates[start].isoformat(),
            "plantEnd": plant_dates[end].isoformat(),
            "harvestStart": harvest_dates[start].isoformat(),
            "harvestEnd": harvest_dates[end].isoformat(),
            "predictedPrice": best_mid,
            "lowerBound": best_lo,
            "upperBound": best_hi,
            "upliftPct": round((best_price - baseline) / baseline * 100.0, 1),
        },
    }


def _monthly_history(crop_id: str, as_of: date, max_months: int = 12) -> list[dict]:
    """Last up-to-`max_months` calendar months of monthly-avg AvgPrice for the
    crop, strictly with ObservationDate <= as_of (no future peeking)."""
    sql = text("""
        SELECT FORMAT(ObservationDate, 'yyyy-MM') AS Month,
               AVG(CAST(AvgPrice AS float))        AS AvgPrice
        FROM CropFeatureDaily
        WHERE CropId = :cid AND ObservationDate <= :asof AND AvgPrice IS NOT NULL
        GROUP BY FORMAT(ObservationDate, 'yyyy-MM')
        ORDER BY Month
    """)
    with get_engine().connect() as conn:
        df = pd.read_sql(sql, conn, params={"cid": crop_id, "asof": as_of})
    if not len(df):
        return []
    df = df.tail(max_months)
    return [{"month": r.Month, "avgPrice": round(float(r.AvgPrice), 2)}
            for r in df.itertuples()]


def _add_months(d: date, months: int) -> date:
    m = d.month - 1 + months
    year = d.year + m // 12
    month = m % 12 + 1
    # clamp day to end of target month
    import calendar
    day = min(d.day, calendar.monthrange(year, month)[1])
    return date(year, month, day)


def timeline(crop_id: str, as_of: date, months: int) -> dict:
    """Monthly history + multi-horizon forecast for one crop.

    Leakage rule: history and forecast use ONLY data with date <= as_of.

    Routing mirrors predict_harvest: when the ML path is promoted AND servable
    (beats_baseline AND _ml_servable()) we anchor the forecast on the served ML
    model (the pooled or residual quantile path, latest feature row <= as_of,
    point-in-time / no lookahead — the residual offset comes from the persisted
    train-only map). Otherwise the central forecast is the crop's historical p50
    (flat) and we say so. In BOTH cases the band WIDENS with horizon (half-spread
    scaled by sqrt(horizon)) because a single-anchor forecast is progressively
    less trustworthy further out.

    The ML path scores the WHAT-IF row for as_of (_whatif_rows), so h=1 returns
    the identical p10/p50/p90 that predict_harvest(crop, as_of) returns — the two
    are drawn on the same screen and must never contradict each other.

    KNOWN AND DELIBERATELY NOT FIXED HERE: the central estimate is the SAME p50 at
    h=1/3/6/12; only the band widens. Making it horizon-sensitive is a design
    decision about what a /timeline point means (the model's label is a single
    harvest-time price at as_of + GrowthPeriodDays, not a term structure), not a
    mechanical change — tracked separately.
    """
    if _PAYLOAD is None:
        raise RuntimeError("No model registered — run train_model_a.py first.")

    crop_id = str(crop_id).lower()  # normalize GUID case (prior bug)
    crop_name, meta_gp = _crop_meta(crop_id)

    history = _monthly_history(crop_id, as_of, max_months=12)

    # Fallback ladder: per-crop (adequate history) -> category -> global. Replaces
    # the old presence-only `have_crop` check, which trusted single-row crops.
    fb_q, tier = _resolve_fallback(crop_id, crop_name)
    p10, p50, p90 = float(fb_q["p10"]), float(fb_q["p50"]), float(fb_q["p90"])

    # Serve the ML path only when promoted AND its artifacts are present/understood
    # here (same guard as predict_harvest; protects the residual/blend trap).
    model_active = bool(_PAYLOAD.get("beats_baseline")) and _ml_servable()
    # v13 history gate: only fetch + score a feature row for crops in the promoted
    # model's served set (mirrors predict_harvest). Non-served crops take the
    # fallback ladder intentionally.
    model_served_crop = _is_model_served(crop_id)
    row = _latest_feature_row(crop_id, as_of) if (model_active and model_served_crop) else None
    # _model_quantiles_safe degrades (returns None) when the row is unscoreable by
    # the incumbent model (e.g. a CropId unseen by an older promoted encoder after
    # a corpus widening) -> serve the fallback ladder instead of 500-ing.
    ml_attempted = bool(model_active and model_served_crop and row is not None)
    # Score the WHAT-IF row for as_of, exactly as predict_harvest does for its plant
    # date (see the what-if section above). This is not cosmetic: /timeline and
    # /predict render on the SAME screen — the hero price comes from /predict and
    # the chart's harvest marker from /timeline's first forecast point — so the two
    # must encode the same date. The anchor row can be days or weeks older than
    # as_of (the feature store lags), and scoring it unchanged made the two numbers
    # diverge. The size of the gap scales with how far the store lags as_of, so it
    # is environment-dependent and NOT reproducible from this repo alone; it was
    # material (double-digit %) on the live v17 payload when this was fixed.
    score_row = row
    if ml_attempted:
        # gp resolved the way the rest of serving does: the feature row's own
        # GrowthPeriodDays, else the CropAgronomyProfiles value from _crop_meta.
        gp = int(row["GrowthPeriodDays"]) if pd.notna(row.get("GrowthPeriodDays")) \
            else meta_gp
        if gp:
            try:
                score_row = _whatif_rows(row, [as_of], int(gp))[0]
            except Exception:
                # Cannot honestly encode as_of -> do NOT quietly score the anchor's
                # stale calendar. Degrade through the ml_failed clamp below, the
                # same way predict_harvest does, so the two endpoints stay in step.
                _log.warning(
                    "What-if row construction failed for crop %s / as_of %s "
                    "-> serving the fallback ladder.", crop_id, as_of,
                    exc_info=True)  # server log only; the API never returns tracebacks
                score_row = None
        # gp missing (no verified agronomy profile): the festival columns are
        # HARVEST-anchored, so there is no honest harvest date to anchor them on.
        # Keep the pre-existing behaviour (score the anchor row as-is) rather than
        # inventing a growth period. Unreachable for a served crop in practice —
        # training needs gp to build the label — so this is a defensive branch.
    q = _model_quantiles_safe(score_row, crop_id) if score_row is not None else None
    if q is not None:
        p10, p50, p90 = float(q["p10"]), float(q["p50"]), float(q["p90"])
        p10, p50, p90 = sorted([p10, p50, p90])
        active = _served_ml_kind()
        confidence = _confidence_for(model_served=True, tier=tier)
        confidence_reason = _reason_for(model_served=True, tier=tier)
        reason_code, reason_params = _reason_code_for(model_served=True, tier=tier)
        explanation = ("ML model forecast from current price, season and recent "
                       "weather; the band widens with horizon to reflect growing "
                       "uncertainty further out.")
    else:
        # Fallback: no servable ML path (or no feature row to score for this crop).
        # Mirror predict_harvest's inert-forecast clamp (previously missing here):
        # a guard-degraded ML attempt (row present, predict failed) or a promoted-
        # model-without-scoreable-row must report "Low", never the tier's trust.
        active = "crop_mean_fallback"
        # Per-crop fallback-predictor switch (chip task_9b1cd894), mirror of
        # predict_harvest: if carry-forward was selected for this crop, anchor the
        # (flat) central forecast on the last observed price <= as_of. One extra
        # lightweight query, only for switched crops. Fail-closed to the incumbent
        # quantiles when no valid price exists. Band still widens with horizon.
        if _fallback_choice(crop_id) == "carry_forward":
            cf = _carry_forward_price(crop_id, as_of)
            if cf is not None:
                p10, p50, p90 = sorted(_recenter_on(_spread_source(crop_id, fb_q), cf))
        # row_missing only when the model IS meant to serve this crop but no row
        # exists; a history-gated-out crop is not "missing a row", it is routed by
        # the gate (not_model_served), so keep those signals distinct.
        row_missing = bool(model_active) and model_served_crop and row is None
        ml_failed = ml_attempted and q is None
        not_model_served = bool(model_active and not model_served_crop)
        confidence = _confidence_for(model_served=False, tier=tier,
                                     row_missing=row_missing, ml_failed=ml_failed,
                                     not_model_served=not_model_served)
        confidence_reason = _reason_for(model_served=False, tier=tier,
                                        row_missing=row_missing, ml_failed=ml_failed,
                                        not_model_served=not_model_served)
        reason_code, reason_params = _reason_code_for(
            model_served=False, tier=tier, row_missing=row_missing,
            ml_failed=ml_failed, not_model_served=not_model_served,
            history_obs=_crop_history_obs(crop_id))
        explanation = (_fallback_explanation(tier) +
                       " The central forecast is flat; the band widens with "
                       "horizon to reflect growing uncertainty.")

    # h=1 reproduces the active predictor's actual [p10, p90] (ML quantiles when
    # served, else the fallback distribution) so the 1-month band matches what
    # /predict returns for the same crop; each side's distance from the median
    # then scales by sqrt(horizon) — honest, asymmetric, growing uncertainty.
    lower_gap = p50 - p10
    upper_gap = p90 - p50
    horizons = [h for h in (1, 3, 6, 12) if h <= months]

    forecast = []
    for h in horizons:
        scale = h ** 0.5
        lower = max(p50 - lower_gap * scale, 0.0)  # price can't be negative
        upper = p50 + upper_gap * scale
        # Rounded through the SAME _ordered_interval as predict_harvest and the
        # harvest-window points. The band maths is untouched (still computed from
        # the UNROUNDED p50 + half-spreads); this only fixes the last step, so a
        # rounding difference can never make the h=1 marker and the hero price on
        # the same screen disagree. At h=1 the scale is 1, so the triple reduces to
        # exactly (p10, p50, p90) — the same three numbers /predict returns.
        lo, mid, hi = _ordered_interval(lower, p50, upper)
        forecast.append({
            "horizonMonths": h,
            "date": _add_months(as_of, h).isoformat(),
            "predictedPrice": mid,
            "lowerBound": lo,
            "upperBound": hi,
        })

    return {
        "cropId": crop_id,
        "cropName": crop_name,
        "asOf": as_of.isoformat(),
        "activePredictor": active,
        "confidence": confidence,
        "confidenceReason": confidence_reason,
        "reasonCode": reason_code,
        "reasonParams": reason_params,
        "fallbackTier": tier,
        "modelVersion": (_META or {}).get("version"),
        "explanation": explanation,
        "history": history,
        "forecast": forecast,
    }


def model_info() -> dict:
    return _META or {"status": "no model registered"}


def crop_readiness() -> dict:
    """Per-crop forecast-readiness map for the app's crop-status colouring.

    Truth MIRRORS the real serving decision above (predict_harvest/timeline):
    a crop is `ready` iff the promoted payload's ML path is active
    (beats_baseline AND _ml_servable()) AND the crop passes the history gate
    (`served_on_crops`). Everything else — thin-history fallback crops, brand-new
    crops absent from the payload, an inactive payload — is NOT ready ("still
    collecting data"). Legacy payloads without `served_on_crops` keep the
    _is_model_served compat rule (every known crop eligible). `nObs` is the
    crop's labelled-row count from fallback.per_crop where available (None on
    old payloads) so callers can show collection progress honestly. Read-only
    over the loaded payload — no DB query, no model call, safe to poll.
    """
    if _PAYLOAD is None:
        return {"modelVersion": None, "minHistoryObs": None, "modelActive": False, "crops": {}}
    model_active = bool(_PAYLOAD.get("beats_baseline")) and _ml_servable()
    served = _served_on_crops()
    per_raw = (_PAYLOAD.get("fallback") or {}).get("per_crop") or {}
    # Keys are lowercased GUIDs by trainer convention; normalize defensively anyway.
    per = {str(k).lower(): (v or {}) for k, v in per_raw.items()}
    crops: dict[str, dict] = {}
    for cid in sorted(set(per.keys()) | (served or set())):
        n_obs = per.get(cid, {}).get("n_obs")
        crops[cid] = {
            "ready": bool(model_active and (served is None or cid in served)),
            "nObs": int(n_obs) if n_obs is not None else None,
        }
    return {
        "modelVersion": (_META or {}).get("version"),
        "minHistoryObs": _min_history_obs(),
        "modelActive": model_active,
        "crops": crops,
    }
