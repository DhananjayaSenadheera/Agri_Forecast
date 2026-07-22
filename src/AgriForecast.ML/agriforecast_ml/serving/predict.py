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

from ..db import get_engine
from ..load import resolve_forecast_gp
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


def _model_quantiles(row, crop_id: str | None = None) -> dict:
    """Quantile predictions for the promoted ML path.

    served_ml_kind == "model":    expm1(model_pred)
    served_ml_kind == "residual": expm1(model_pred + log1p(offset)) where offset
                                  is the persisted per-crop train-only mean.
    """
    X = _build_X(row)
    kind = _served_ml_kind()
    if kind == "residual":
        offset = _residual_offset(crop_id)
        log_off = float(np.log1p(offset))
        out = {}
        for q, mdl in _PAYLOAD["residual_models"].items():
            out[q] = float(np.expm1(mdl.predict(X)[0] + log_off))
        return out
    # default pooled "model" path
    out = {}
    for q, mdl in _PAYLOAD["models"].items():
        out[q] = float(np.expm1(mdl.predict(X))[0])
    return out


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
    q = _model_quantiles_safe(row, crop_id) if ml_attempted else None
    if q is not None:
        p10, p50, p90 = q["p10"], q["p50"], q["p90"]
        active = _served_ml_kind()
        confidence = _confidence_for(model_served=True, tier=tier)
        confidence_reason = _reason_for(model_served=True, tier=tier)
        reason_code, reason_params = _reason_code_for(model_served=True, tier=tier)
        explanation = "ML model forecast from current price, season and recent weather."
        # Farmer-meaningful factor codes from the ACTUAL promoted model (SHAP on
        # the served p50 model), aggregated per code. Deterministic per input.
        top_factors = explain.top_factor_codes(row, _PAYLOAD, top_n=4)
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

    p10, p50, p90 = sorted([round(p10, 2), round(p50, 2), round(p90, 2)])
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
    """
    if _PAYLOAD is None:
        raise RuntimeError("No model registered — run train_model_a.py first.")

    crop_id = str(crop_id).lower()  # normalize GUID case (prior bug)
    crop_name, _ = _crop_meta(crop_id)

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
    q = _model_quantiles_safe(row, crop_id) if ml_attempted else None
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
        lower = round(max(p50 - lower_gap * scale, 0.0), 2)  # price can't be negative
        upper = round(p50 + upper_gap * scale, 2)
        forecast.append({
            "horizonMonths": h,
            "date": _add_months(as_of, h).isoformat(),
            "predictedPrice": round(p50, 2),
            "lowerBound": lower,
            "upperBound": upper,
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
