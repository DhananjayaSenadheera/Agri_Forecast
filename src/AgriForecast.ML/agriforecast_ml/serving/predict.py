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

# Serving reads agronomy from CropAgronomyProfiles: an unverified profile means no
# served horizon, so the crop takes the crop-mean fallback.


# Load the promoted payload once at import (serving is read-only).
_PAYLOAD, _META = registry.load_promoted()

# Used only when the promoted payload predates fallback['min_history_obs'], so an old
# payload degrades to this constant instead of crashing.
_DEFAULT_MIN_HISTORY_OBS = 365


def _min_history_obs() -> int:
    fb = (_PAYLOAD or {}).get("fallback") or {}
    v = fb.get("min_history_obs")
    return int(v) if v is not None else _DEFAULT_MIN_HISTORY_OBS


# History gate. The trainer fits the pooled model only on crops with enough labelled
# rows and ships that set as served_on_crops. Serving routes any other crop to the
# fallback ladder on purpose, so it never reaches the model and cannot trigger the
# unseen-category error - the crash guard stays a pure backstop. Old payloads have no
# served_on_crops key: this then returns None and every crop counts as eligible.
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


# The trainer backtests, per fallback-served crop, whether carry-forward (the last
# observed AvgPrice) beats the recency-mean incumbent, and ships the winners in the
# signed payload under fallback['choice']. Serving re-centres the fallback interval
# on the chosen predictor. Fail-closed in every degenerate case (no key, old payload,
# unknown crop, missing price, any error): the crop keeps the incumbent behaviour.
# Only the central p50 and the band centre move; every string field stays the same.
_SERVABLE_FALLBACK_CHOICES = {"carry_forward"}


def _fallback_choice(crop_id: str) -> str | None:
    """The shipped fallback-predictor choice for this crop, or None (= keep the
    recency-mean/category incumbent). Old payloads without the key -> None."""
    choice = ((_PAYLOAD or {}).get("fallback") or {}).get("choice") or {}
    ch = choice.get(str(crop_id).lower())
    return ch if ch in _SERVABLE_FALLBACK_CHOICES else None


# Max age of the carry-forward anchor, matching the 60-day macro staleness convention.
# Past the cap we fail closed to the incumbent tier rather than serve a stale level.
_CARRY_FORWARD_STALENESS_DAYS = 60


def _within_carry_forward_staleness(obs_date, as_of: date) -> bool:
    """True iff the anchor's observation is within the staleness cap of the
    request reference date. Pure/point-in-time (age measured against as_of, never
    a future date)."""
    age = (pd.Timestamp(as_of) - pd.Timestamp(obs_date)).days
    return age <= _CARRY_FORWARD_STALENESS_DAYS


def _last_avgprice_at_or_before(crop_id: str, as_of: date,
                                max_back_days: int | None = None):
    """THE price series: newest non-null CropFeatureDaily.AvgPrice at or before as_of.

    This is the one series the training label is a shift of (`price.shift(-gp)` over the
    ffilled AvgPrice column in features.build_crop_features). Everything that needs "the
    price that was known at date D" - the serving carry-forward anchor and the forecast
    snapshot's matured actual - MUST come through here, or serve and score would silently
    drift onto different definitions of the same number.

    max_back_days, when given, refuses a value older than that many days before as_of.
    The snapshot maturing pass passes SNAPSHOT_MATCH_BACK_DAYS (== features._FFILL_LIMIT)
    so its actual is exactly the carry the label's ffill would have made. Left None the
    scan is unbounded and the caller applies its own staleness rule.

    Returns (price, observation_date) or None when nothing qualifies. No positivity or
    staleness filtering happens here - those are caller policy, not series identity.
    """
    clauses = ["CropId = :cid", "ObservationDate <= :asof", "AvgPrice IS NOT NULL"]
    params: dict = {"cid": str(crop_id).lower(), "asof": as_of}
    if max_back_days is not None:
        clauses.append("ObservationDate >= :floor")
        params["floor"] = as_of - timedelta(days=int(max_back_days))
    sql = text(
        "SELECT TOP 1 AvgPrice, ObservationDate FROM CropFeatureDaily WHERE "
        + " AND ".join(clauses)
        + " ORDER BY ObservationDate DESC"
    )
    with get_engine().connect() as conn:
        df = pd.read_sql(sql, conn, params=params)
    if not len(df) or pd.isna(df["AvgPrice"].iloc[0]):
        return None
    return float(df["AvgPrice"].iloc[0]), df["ObservationDate"].iloc[0]


def _carry_forward_price(crop_id: str, as_of: date) -> float | None:
    """Last non-null positive AvgPrice at or before as_of, if it is inside the staleness cap.

    Returns None when there is no scoreable price, or the newest one is older than the
    cap, so the caller fails closed to the incumbent. Both endpoints use this so they
    agree on the anchor. The read itself is _last_avgprice_at_or_before - the shared
    series definition - and only the staleness/positivity policy lives here.
    """
    hit = _last_avgprice_at_or_before(crop_id, as_of)
    if hit is None:
        return None
    value, obs_date = hit
    if not _within_carry_forward_staleness(obs_date, as_of):
        return None
    return value if value > 0 else None


def _spread_source(crop_id: str, fb_q: dict) -> dict:
    """The band half-spreads to keep when re-centring on a switched predictor.

    Prefers the crop's own per-crop quantiles over the resolved tier's, which at the
    category tier pools many crops and is far too wide. Falls back to the tier when the
    crop has no entry of its own.
    """
    per = ((_PAYLOAD or {}).get("fallback") or {}).get("per_crop", {}).get(crop_id)
    return per if per is not None else fb_q


def _recenter_on(spread_q: dict, centre: float) -> tuple[float, float, float]:
    """Re-centre the fallback interval on centre, keeping spread_q's half-spreads.

    The band is deliberately absolute-width - the crop's historical rupee half-spreads,
    not a percentage of the new level - so a low current price still carries a realistic
    band. The lower bound is clamped at 0; ordering is left to the caller.
    """
    lower_gap = max(float(spread_q["p50"]) - float(spread_q["p10"]), 0.0)
    upper_gap = max(float(spread_q["p90"]) - float(spread_q["p50"]), 0.0)
    return max(centre - lower_gap, 0.0), centre, centre + upper_gap


def _resolve_fallback(crop_id: str, crop_name: str | None = None) -> tuple[dict, str]:
    """Fallback ladder: per-crop (if n_obs >= threshold) -> category -> global.

    Returns (quantile_dict, tier). A per-crop entry with no n_obs key comes from an old
    payload and counts as adequate history, so we never invent a cold-start flag the
    trained data cannot support.
    """
    fb = _PAYLOAD["fallback"]
    per = fb["per_crop"].get(crop_id)
    if per is not None:
        n_obs = per.get("n_obs")
        # No n_obs means an old payload: keep the legacy behaviour and trust the per-crop row.
        if n_obs is None or int(n_obs) >= _min_history_obs():
            return per, "crop"
    # Per-crop entry absent or thin: try the category rung.
    cat = category_for(crop_id, crop_name)
    by_cat = fb.get("by_category") or {}
    if cat and cat in by_cat:
        return by_cat[cat], "category"
    return fb["global"], "global"


# Confidence rungs. 'Low' and 'Medium' keep their exact .NET MlContract spelling:
# 'Low' caps the recommendation, 'Medium' does not. 'High' is additive and safe - the
# .NET matrix only lowers trust on Low/fallback, so 'High' can never make an outcome
# worse. It is emitted only when the ML model serves a crop that has adequate own
# history (tier == 'crop').
def _confidence_for(model_served: bool, tier: str, row_missing: bool = False,
                    ml_failed: bool = False, not_model_served: bool = False) -> str:
    # row_missing: the fallback was taken because no scoreable feature row exists at the
    # plant date. The forecast is inert until retrain, so clamp to 'Low' whatever tier
    # resolved. The exact 'Low' spelling is load-bearing (.NET MlContract fails closed on it).
    # ml_failed: the ML path was attempted but the predict call failed - same clamp, so a
    # trained-crop tier can never report Medium/High for a forecast the model refused.
    # not_model_served: the history gate routed this crop to the baseline. The clamp is
    # explicit so a future divergence of the two thresholds cannot let a gated-out crop
    # report Medium/High.
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
        # History gate: too little history to train the ML model on this crop, so it is routed
        # to the baseline on purpose - distinct from ml_failed, an unexpected failure.
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


# Machine-stable reason CODES; the prose fields are unchanged. Every distinct
# _reason_for branch gets one snake_case code plus params so the trilingual FE does
# not have to parse English. Branch order MUST mirror _reason_for exactly.
def _reason_code_for(model_served: bool, tier: str, row_missing: bool = False,
                     ml_failed: bool = False, not_model_served: bool = False,
                     history_obs: int | None = None) -> tuple[str, dict]:
    # First match wins, so callers must keep the three flags mutually exclusive (they do).
    # The branch order matches _reason_for.
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

    GrowthPeriodDays comes from CropAgronomyProfiles, not the dropped Crops column, via a
    LEFT JOIN so a crop with no profile still returns its name with gp=None.

    A crop gets a served harvest horizon only if its profile is owner-verified AND has a
    usable GrowthPeriodDays; otherwise gp is None and the caller serves the crop-mean
    fallback with no horizon. Routes through load.resolve_forecast_gp so serving applies
    the same gate as the feature build.
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


# ML kinds this module knows how to serve. A promoted version advertising anything
# else must not be served as an ML model - fall back safely instead of guessing.
_SERVABLE_ML_KINDS = {"model", "residual"}


def _served_ml_kind() -> str:
    # Older payloads predate served_ml_kind; default to the pooled "model" path.
    return str((_PAYLOAD or {}).get("served_ml_kind", "model"))


def _ml_servable() -> bool:
    """True if the promoted ML path is one we can actually serve with persisted artifacts.

    Serving must never honour an ML kind whose artifacts are not in the registry.
    """
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
    # The serve-time input frame is built in serving/build_x.py, shared with explain.py so
    # coercion and NaN discipline cannot drift between the predict and SHAP paths.
    return build_x(row, _PAYLOAD)


def _residual_offset(crop_id: str) -> float:
    """Per-crop residual offset, read from the persisted train-only map.

    Leakage-safe: the map was fixed at training time and serving never recomputes it.
    """
    offsets = _PAYLOAD["residual_offsets"]
    return float(offsets.get(str(crop_id).lower(), _PAYLOAD["residual_offset_global"]))


def _model_quantiles_safe(row, crop_id: str | None = None) -> dict | None:
    """Wrap _model_quantiles so an unscoreable row degrades to the fallback ladder.

    CropId is a categorical feature, so XGBoost raises on a CropId it never saw during
    training. That happens whenever the promoted model was trained on a smaller crop set
    than the feature store now covers: the new crop has a feature row and reaches the ML
    path, but the incumbent encoder does not know it. Return None instead of crashing and
    the caller serves the crop-mean fallback. Also catches other predict-time artifact
    mismatches.
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
    """Quantile predictions for an already-built model-input frame of 1..N rows.

    served_ml_kind 'model' gives expm1(pred); 'residual' gives
    expm1(pred + log1p(offset)) using the persisted per-crop train-only offset. The
    inverse transform lives here once so the single-shot path and the multi-date what-if
    sweep can never disagree about how a raw output becomes a rupee price.
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


# The what-if row: the one definition of 'score this crop for planting date D'.
# /predict asks that once and /harvest-window asks it for every candidate date, so both
# must build the model input the same way or they contradict each other on screen.
#
# _latest_feature_row(crop, D) returns the newest row with ObservationDate <= D, which
# for a future D is today's row. Scoring it unchanged answers 'what if I plant on the
# anchor's observation date', not 'what if I plant on D'. So each requested date gets a
# what-if row: the observed columns (price, lags, weather, macro, FX, sentiment, policy,
# spreads) stay FROZEN at the anchor's values, while the calendar and harvest-anchored
# festival columns are RECOMPUTED with the same code the training rows were built with.
#
# The recompute is not lookahead: those columns are pure functions of a date plus a
# calendar gazetted in advance. Nothing that must be observed - a price, a rainfall
# total, a CPI print - is ever moved forward. Past dates get the recompute too, since
# encoding the date actually asked about beats inheriting the anchor's older encoding.
# Festival-calendar cache. _whatif_rows runs on every /predict and /timeline request,
# so re-reading the ~64-row FestivalCalendarEntries per request would put a DB
# round-trip on the two hottest endpoints. Cached process-wide, like the payload.
#
# Tradeoff: a gazette update does not reach a running process until it restarts - the
# same lifecycle the promoted payload already has, so model and calendar refresh together.
#
# An EMPTY calendar is deliberately NOT cached: load_festivals() swallows DB errors, so
# empty is indistinguishable from unreachable and caching it would pin a transient
# outage into festival-blind forecasts. Tests reset _FESTIVAL_WINDOWS to None.
_FESTIVAL_WINDOWS: tuple | None = None


def _festival_windows_cached():
    """(events_arr, leadup_windows) for the what-if build — see the cache note."""
    global _FESTIVAL_WINDOWS
    if _FESTIVAL_WINDOWS is not None:
        return _FESTIVAL_WINDOWS
    try:
        events_arr, windows = features._festival_windows(load_festivals())
    except Exception:
        # A missing calendar must not fail the request: degrade to 'no festivals known', as the
        # feature build does. Not cached, so the next request retries.
        _log.warning("Festival calendar unavailable for the what-if row build "
                     "— seasonality only.", exc_info=True)
        return features._festival_windows(None)
    if events_arr.size:
        _FESTIVAL_WINDOWS = (events_arr, windows)
    return events_arr, windows


def _whatif_rows(anchor_row, plant_dates, gp: int) -> list[dict]:
    """One what-if row (a plain dict, ready for build_x) per planting date.

    anchor_row is a CropFeatureDaily row whose non-calendar columns are frozen. gp is the
    crop's growth period and anchors the festival features on the HARVEST date, matching
    how the training label was built.

    Raises only on a genuinely broken date computation; every caller treats that as
    'cannot honestly encode this date' and degrades rather than scoring a stale calendar.
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

    One rounding rule for both date-answering endpoints, so /predict for date D and the
    /harvest-window point for date D can never print different numbers. The quantile
    models are fitted independently and can cross, and sorting is the tie-break. Display
    only: harvest_window still ranks on the raw p50s.
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
    # Serve the ML path only when the gate promoted it AND its artifacts are present and
    # understood here.
    model_active = bool(_PAYLOAD.get("beats_baseline")) and _ml_servable()

    # top_factors is emitted only on the model-served path; the fallback path omits the
    # field entirely and the FE shows an honest 'no breakdown' note.
    top_factors: list[dict] | None = None
    # Resolve the fallback rung once: it also drives confidence when ML serves, so a
    # thin-history crop is not over-trusted.
    fb_q, tier = _resolve_fallback(crop_id, crop_name)
    # History gate: the ML path is attempted only for crops in the promoted model's served
    # set. A non-served crop skips the model and gets a distinct, non-ml_failed reason, so
    # its fallback reads as a deliberate decision.
    model_served_crop = _is_model_served(crop_id)
    ml_attempted = bool(model_active and model_served_crop and row is not None and gp)
    # Score the WHAT-IF row for the requested plant date, never the anchor row as it
    # happened to be observed. Otherwise every plant date returns the same price and
    # /predict contradicts /harvest-window, which always recomputed the date columns.
    score_row = None
    if ml_attempted:
        try:
            score_row = _whatif_rows(row, [plant_date], int(gp))[0]
        except Exception:
            # We cannot honestly encode the requested date, so do not quietly score the
            # anchor's stale calendar. Degrade through the same ml_failed clamp (Low).
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
        # Farmer-meaningful factor codes from SHAP on the served p50 model. Must be the
        # same what-if row the quantiles came from, or the explanation would describe a
        # different input than the price printed above it.
        top_factors = explain.top_factor_codes(score_row, _PAYLOAD, top_n=4)
    else:
        p10, p50, p90 = fb_q["p10"], fb_q["p50"], fb_q["p90"]
        active = "crop_mean_fallback"
        # If the payload selected carry-forward for this crop, re-centre the interval on
        # the last non-null AvgPrice inside the staleness cap. Uses _carry_forward_price
        # rather than row['AvgPrice'], whose newest row may be NULL, so predict_harvest
        # and timeline agree. Fail-closed: no usable price keeps the incumbent quantiles.
        if _fallback_choice(crop_id) == "carry_forward":
            cf = _carry_forward_price(crop_id, plant_date)
            if cf is not None:
                p10, p50, p90 = _recenter_on(_spread_source(crop_id, fb_q), cf)
        # No scoreable feature row at the plant date means the prediction is inert, so
        # force 'Low' trust whatever tier resolved (fallbackTier still reports the tier).
        # Same clamp when the ML path was attempted but the predict call failed.
        row_missing = row is None
        ml_failed = ml_attempted and q is None
        # not_model_served: the model is live and a row exists, but this crop is outside
        # the history gate - a deliberate baseline route, distinct from ml_failed. Only
        # meaningful when the row and gp exist.
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


# Best harvest window: 'when should I plant to sell into a good price?'
#
# The model maps features known at D to the price at D + GrowthPeriodDays, so scoring a
# row already asks 'if I plant on D, what will the harvest fetch?'. Sweeping D over the
# next few months is a question the model was built to answer.
#
# The trap: _latest_feature_row(crop, D) returns the same row for every future D, so a
# naive sweep re-scores identical inputs and picks a winner out of floating-point noise.
# The fix is one what-if row per candidate date from the shared _whatif_rows() above, so
# this sweep and /predict return the same number for the same date.
#
# What the farmer is told: holding today's market and weather constant, this is how the
# seasonal and festival-demand structure of the year prices the harvest. It ranks
# TIMING, not future weather, and the API says exactly that in `explanation`.
#
# Any path that cannot support that claim returns rankable=False with a reason code
# instead of a window: a farmer can plant on a date they cannot un-plant.

_WINDOW_HORIZON_DAYS = 90     # default sweep length (candidate planting dates)
_WINDOW_MAX_HORIZON = 365  # hard cap: one seasonal cycle; beyond it the frozen price anchor is meaningless
_WINDOW_LEN_DEFAULT = 14      # window length when the crop has no HarvestWindowDays
_WINDOW_LEN_MIN = 7           # a 1-3 day "window" is not actionable advice
_WINDOW_LEN_MAX = 30
# Peak-to-trough spread, as a fraction of the median forecast, below which we refuse to
# name a best window: a curve this flat carries noise, not a timing signal.
_WINDOW_FLAT_EPS = 0.005


def _window_len_for(row) -> int:
    """Window length in days: the crop's own HarvestWindowDays when curated, else a default.

    Clamped so the answer is actionable - neither a single day nor a whole season.
    """
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
    """Rank candidate planting dates over the next horizon_days by forecast harvest price,
    and name the best contiguous window.

    Returns rankable=False - never a fabricated window - when the crop has no harvest
    horizon, the promoted model cannot serve it, or the curve is too flat to carry a
    timing signal.
    """
    if _PAYLOAD is None:
        raise RuntimeError("No model registered — run train_model_a.py first.")

    crop_id = str(crop_id).lower()  # GUID case varies by caller; normalize
    horizon_days = max(1, min(int(horizon_days), _WINDOW_MAX_HORIZON))

    row = _latest_feature_row(crop_id, as_of)
    crop_name, gp = (row["CropName"], int(row["GrowthPeriodDays"])) if row is not None \
        and pd.notna(row.get("GrowthPeriodDays")) else _crop_meta(crop_id)

    # Honesty gates, ordered so the reason given is the most specific one.
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

    # Build the what-if rows, one per candidate planting date.
    plant_dates = [as_of + timedelta(days=i) for i in range(horizon_days + 1)]
    harvest_dates = [d + timedelta(days=gp) for d in plant_dates]

    # The row build sits inside the same guard as the scoring call: failing to encode the
    # candidate dates is the same class of event as failing to score them, and this
    # endpoint should degrade like /predict and /timeline rather than 500.
    try:
        # Per-row build_x so coercion and NaN discipline match the single-shot path; batched
        # below only so we predict once instead of N times.
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

    # Flat-curve gate: refuse to rank noise.
    level = float(np.median(p50s))
    spread = float(np.max(p50s) - np.min(p50s))
    if level <= 0 or (spread / level) < _WINDOW_FLAT_EPS:
        return _window_unavailable(
            crop_id, crop_name, as_of, gp, "flat_curve",
            "For this crop the forecast is effectively the same whenever you "
            "plant, so there is no better or worse time to aim for.")

    # Pick the best contiguous window.
    window_days = _window_len_for(row)
    span = min(window_days, len(p50s))
    means = np.convolve(p50s, np.ones(span) / span, mode="valid")
    start = int(np.argmax(means))
    end = start + span - 1

    # Uplift is stated against the average date in the swept horizon - 'better than planting
    # at a typical time' - not against the worst date, which would inflate it.
    baseline = float(np.mean(p50s))
    best_price = float(means[start])

    # Rounded through the same _ordered_interval as predict_harvest, so a point here and
    # /predict for that point's date print identical numbers.
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

    # The best block's band is the window average of the p10s and p90s, rounded through
    # _ordered_interval like everything else so lowerBound <= predictedPrice <= upperBound
    # still holds: averaging cannot fix crossed quantiles.
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
    """Monthly history plus a multi-horizon forecast for one crop.

    History and forecast use only data with date <= as_of.

    Routing mirrors predict_harvest: when the ML path is promoted and servable we anchor
    the forecast on the served model, otherwise the central forecast is the crop's
    historical p50 and we say so. Either way the band widens with horizon (half-spread
    scaled by sqrt(horizon)), because a single-anchor forecast is less trustworthy
    further out.

    The ML path scores the what-if row for as_of, so h=1 returns the same p10/p50/p90
    that predict_harvest(crop, as_of) returns - the two are drawn on one screen.

    Known and deliberately not fixed here: the central estimate is the same p50 at every
    horizon and only the band widens. Making it horizon-sensitive is a design decision
    about what a /timeline point means, tracked separately.
    """
    if _PAYLOAD is None:
        raise RuntimeError("No model registered — run train_model_a.py first.")

    crop_id = str(crop_id).lower()  # normalize GUID case (prior bug)
    crop_name, meta_gp = _crop_meta(crop_id)

    history = _monthly_history(crop_id, as_of, max_months=12)

    # Fallback ladder: per-crop (adequate history) -> category -> global.
    fb_q, tier = _resolve_fallback(crop_id, crop_name)
    p10, p50, p90 = float(fb_q["p10"]), float(fb_q["p50"]), float(fb_q["p90"])

    # Serve the ML path only when promoted AND its artifacts are present and understood
    # here - the same guard predict_harvest uses.
    model_active = bool(_PAYLOAD.get("beats_baseline")) and _ml_servable()
    # History gate: only fetch and score a feature row for crops in the promoted model's
    # served set, mirroring predict_harvest.
    model_served_crop = _is_model_served(crop_id)
    row = _latest_feature_row(crop_id, as_of) if (model_active and model_served_crop) else None
    # _model_quantiles_safe returns None when the incumbent model cannot score the row
    # (e.g. a CropId it never saw), so we serve the fallback instead of 500-ing.
    ml_attempted = bool(model_active and model_served_crop and row is not None)
    # Score the WHAT-IF row for as_of, exactly as predict_harvest does for its plant date.
    # /timeline and /predict render on the same screen - the hero price from /predict, the
    # chart's harvest marker from /timeline's first point - so both must encode the same
    # date. The anchor row can be days or weeks older than as_of, and scoring it unchanged
    # made the two numbers diverge by double-digit percentages on the live payload.
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
                # Cannot honestly encode as_of, so do not quietly score the anchor's stale
                # calendar. Degrade through the ml_failed clamp, as predict_harvest does.
                _log.warning(
                    "What-if row construction failed for crop %s / as_of %s "
                    "-> serving the fallback ladder.", crop_id, as_of,
                    exc_info=True)  # server log only; the API never returns tracebacks
                score_row = None
        # No gp (no verified agronomy profile): the festival columns are harvest-anchored, so
        # there is no honest harvest date to anchor them on. Keep the old behaviour of scoring
        # the anchor row rather than inventing a growth period. Defensive: training needs gp.
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
        # Fallback: no servable ML path, or no feature row to score for this crop.
        # Mirror predict_harvest's inert-forecast clamp: a guard-degraded ML attempt, or a
        # promoted model with no scoreable row, must report 'Low', never the tier's trust.
        active = "crop_mean_fallback"
        # Mirror of predict_harvest: if carry-forward was selected for this crop, anchor the
        # flat central forecast on the last observed price <= as_of. Fail-closed to the
        # incumbent quantiles when no valid price exists; the band still widens with horizon.
        if _fallback_choice(crop_id) == "carry_forward":
            cf = _carry_forward_price(crop_id, as_of)
            if cf is not None:
                p10, p50, p90 = sorted(_recenter_on(_spread_source(crop_id, fb_q), cf))
        # row_missing only when the model is meant to serve this crop but no row exists; a
        # gated-out crop is routed by the gate (not_model_served), so keep the two distinct.
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

    # h=1 reproduces the active predictor's actual [p10, p90] so the 1-month band matches
    # what /predict returns; each side's distance from the median then scales by sqrt(horizon).
    lower_gap = p50 - p10
    upper_gap = p90 - p50
    horizons = [h for h in (1, 3, 6, 12) if h <= months]

    forecast = []
    for h in horizons:
        scale = h ** 0.5
        lower = max(p50 - lower_gap * scale, 0.0)  # price can't be negative
        upper = p50 + upper_gap * scale
        # Rounded through the same _ordered_interval as predict_harvest and the harvest-window
        # points, so rounding cannot make the h=1 marker disagree with the hero price. At h=1
        # the scale is 1, so the triple is exactly (p10, p50, p90).
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

    Mirrors the real serving decision: a crop is ready only when the promoted payload's ML
    path is active AND the crop passes the history gate. Thin-history fallback crops, crops
    absent from the payload and an inactive payload are all not ready. Legacy payloads
    without served_on_crops keep the compat rule that every known crop is eligible. nObs
    comes from fallback.per_crop where available so callers can show collection progress.
    Read-only over the loaded payload - no DB query, no model call, safe to poll.
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
