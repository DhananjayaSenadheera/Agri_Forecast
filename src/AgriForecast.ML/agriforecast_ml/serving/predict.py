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
                    ml_failed: bool = False) -> str:
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
    if row_missing or ml_failed:
        return "Low"
    if tier == "global":
        return "Low"          # unknown / no category prior — genuine cold start
    if model_served and tier == "crop":
        return "High"
    if tier == "category":
        return "Low"          # borrowed prior, thin own history -> honest low trust
    return "Medium"           # adequate own history, fallback or ML


def _reason_for(model_served: bool, tier: str, row_missing: bool = False,
                ml_failed: bool = False) -> str:
    """Human-readable justification for the confidence rung (additive field)."""
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

    top_factors: list[dict] = []
    # Resolve the fallback rung once (used for confidence even when ML serves, so
    # a thin-history ML-covered crop is not over-trusted).
    fb_q, tier = _resolve_fallback(crop_id, crop_name)
    ml_attempted = bool(model_active and row is not None and gp)
    q = _model_quantiles_safe(row, crop_id) if ml_attempted else None
    if q is not None:
        p10, p50, p90 = q["p10"], q["p50"], q["p90"]
        active = _served_ml_kind()
        confidence = _confidence_for(model_served=True, tier=tier)
        confidence_reason = _reason_for(model_served=True, tier=tier)
        explanation = "ML model forecast from current price, season and recent weather."
        top_factors = explain.top_factors(row, _PAYLOAD, top_n=5)
    else:
        p10, p50, p90 = fb_q["p10"], fb_q["p50"], fb_q["p90"]
        active = "crop_mean_fallback"
        # When the fallback is taken because there is NO scoreable feature row as of
        # the plant date, the prediction is un-scoreable/inert -> force "Low" trust
        # regardless of the resolved tier (fallbackTier still reports the real tier).
        # Same clamp when the ML path was attempted but the predict call failed
        # (_model_quantiles_safe -> None, e.g. incumbent encoder predates this crop).
        row_missing = row is None
        ml_failed = ml_attempted and q is None
        confidence = _confidence_for(model_served=False, tier=tier,
                                     row_missing=row_missing, ml_failed=ml_failed)
        confidence_reason = _reason_for(model_served=False, tier=tier,
                                        row_missing=row_missing, ml_failed=ml_failed)
        explanation = _fallback_explanation(tier)

    p10, p50, p90 = sorted([round(p10, 2), round(p50, 2), round(p90, 2)])
    return {
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
        "fallbackTier": tier,
        "activePredictor": active,
        "modelVersion": (_META or {}).get("version"),
        "explanation": explanation,
        "topFactors": top_factors,
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
    row = _latest_feature_row(crop_id, as_of) if model_active else None
    # _model_quantiles_safe degrades (returns None) when the row is unscoreable by
    # the incumbent model (e.g. a CropId unseen by an older promoted encoder after
    # a corpus widening) -> serve the fallback ladder instead of 500-ing.
    ml_attempted = bool(model_active and row is not None)
    q = _model_quantiles_safe(row, crop_id) if ml_attempted else None
    if q is not None:
        p10, p50, p90 = float(q["p10"]), float(q["p50"]), float(q["p90"])
        p10, p50, p90 = sorted([p10, p50, p90])
        active = _served_ml_kind()
        confidence = _confidence_for(model_served=True, tier=tier)
        confidence_reason = _reason_for(model_served=True, tier=tier)
        explanation = ("ML model forecast from current price, season and recent "
                       "weather; the band widens with horizon to reflect growing "
                       "uncertainty further out.")
    else:
        # Fallback: no servable ML path (or no feature row to score for this crop).
        # Mirror predict_harvest's inert-forecast clamp (previously missing here):
        # a guard-degraded ML attempt (row present, predict failed) or a promoted-
        # model-without-scoreable-row must report "Low", never the tier's trust.
        active = "crop_mean_fallback"
        row_missing = bool(model_active) and row is None
        ml_failed = ml_attempted and q is None
        confidence = _confidence_for(model_served=False, tier=tier,
                                     row_missing=row_missing, ml_failed=ml_failed)
        confidence_reason = _reason_for(model_served=False, tier=tier,
                                        row_missing=row_missing, ml_failed=ml_failed)
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
        "fallbackTier": tier,
        "modelVersion": (_META or {}).get("version"),
        "explanation": explanation,
        "history": history,
        "forecast": forecast,
    }


def model_info() -> dict:
    return _META or {"status": "no model registered"}
