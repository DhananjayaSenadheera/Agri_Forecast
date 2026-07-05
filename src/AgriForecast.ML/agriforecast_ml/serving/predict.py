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
from ..registry import registry
from . import explain

_log = logging.getLogger(__name__)

# R2 Step 2.3: serving reads agronomy from CropAgronomyProfiles. Emit ONE
# aggregate WARN (not per request) when an unverified profile drives a served
# horizon, so the pre-Step-5 transition is visible. Step 5.3 makes this a hard
# skip (IsVerified-strict exclusion).
_unverified_warned = False


def _warn_unverified_once() -> None:
    global _unverified_warned
    if not _unverified_warned:
        _unverified_warned = True
        _log.warning(
            "Serving a forecast horizon from an UNVERIFIED agronomy profile "
            "(IsVerified=0). Expected pre-Step-5; legacy-copied values. "
            "Step 5.3 flips the exclusion predicate to IsVerified-strict."
        )


# Load the promoted payload once at import (serving is read-only).
_PAYLOAD, _META = registry.load_promoted()


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

    A NULL/absent GrowthPeriodDays => this crop is excluded from ML forecasting
    (the R2 exclusion predicate); the caller degrades to the crop-mean fallback.

    IsVerified is read for visibility: if an unverified profile drives a served
    horizon, WARN once (aggregate, module-level) so the pre-Step-5 transition is
    visible. TODO(R2 Step 5.3): flip to IsVerified-strict (unverified => excluded).
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
    gp = df["GrowthPeriodDays"].iloc[0]
    gp = int(gp) if pd.notna(gp) else None
    if gp is not None and df["IsVerified"].iloc[0] != True:  # noqa: E712
        _warn_unverified_once()
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
    # Serving contract — missing features stay NaN, never 0.0. A missing
    # sentiment column (MeanSentiment / DroughtRatio / FloodRatio / PolicyRatio)
    # means "no news signal as of this date"; 0.0 is a *measured* VADER-neutral
    # value, which is semantically different. Training (_attach_sentiment) leaves
    # these NaN and XGBoost handles NaN natively (learned default split
    # direction). So errors="coerce" below is deliberate — do NOT add .fillna(0).
    cols = _PAYLOAD["feature_cols"]
    X = pd.DataFrame([{c: row.get(c) for c in cols}])
    for c in cols:
        if c in _PAYLOAD["categorical"]:
            X[c] = X[c].astype("category")
        else:
            X[c] = pd.to_numeric(X[c], errors="coerce").astype("float64")
    return X


def _residual_offset(crop_id: str) -> float:
    """Per-crop residual offset, looked up from the PERSISTED train-only map.
    Point-in-time / leakage-safe: the map was fixed at training time from data
    at/before train; serving never recomputes it from current/future data."""
    offsets = _PAYLOAD["residual_offsets"]
    return float(offsets.get(str(crop_id).lower(), _PAYLOAD["residual_offset_global"]))


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
    if model_active and row is not None and gp:
        q = _model_quantiles(row, crop_id)
        p10, p50, p90 = q["p10"], q["p50"], q["p90"]
        active, confidence = _served_ml_kind(), "Medium"
        explanation = "ML model forecast from current price, season and recent weather."
        top_factors = explain.top_factors(row, _PAYLOAD, top_n=5)
    else:
        fb = _PAYLOAD["fallback"]
        per = fb["per_crop"].get(crop_id) or fb["global"]
        p10, p50, p90 = per["p10"], per["p50"], per["p90"]
        active = "crop_mean_fallback"
        confidence = "Low" if row is None else "Medium"
        explanation = ("Based on this crop's historical harvest-price distribution. "
                       "(The ML model is not yet more accurate than this baseline at current data volume.)")

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

    fb = _PAYLOAD["fallback"]
    per = fb["per_crop"].get(crop_id) or fb["global"]
    p10, p50, p90 = float(per["p10"]), float(per["p50"]), float(per["p90"])
    have_crop = crop_id in fb["per_crop"]

    # Serve the ML path only when promoted AND its artifacts are present/understood
    # here (same guard as predict_harvest; protects the residual/blend trap).
    model_active = bool(_PAYLOAD.get("beats_baseline")) and _ml_servable()
    row = _latest_feature_row(crop_id, as_of) if model_active else None
    if model_active and row is not None:
        q = _model_quantiles(row, crop_id)  # residual or pooled, per served_ml_kind
        p10, p50, p90 = float(q["p10"]), float(q["p50"]), float(q["p90"])
        p10, p50, p90 = sorted([p10, p50, p90])
        active, confidence = _served_ml_kind(), "Medium"
        explanation = ("ML model forecast from current price, season and recent "
                       "weather; the band widens with horizon to reflect growing "
                       "uncertainty further out.")
    else:
        # Fallback: no servable ML path (or no feature row to score for this crop).
        active = "crop_mean_fallback"
        confidence = "Medium" if have_crop else "Low"
        explanation = ("Based on this crop's historical harvest-price distribution. "
                       "(The ML model is not yet more accurate than this baseline at "
                       "current data volume, so the central forecast is flat; the band "
                       "widens with horizon to reflect growing uncertainty.)")

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
        "modelVersion": (_META or {}).get("version"),
        "explanation": explanation,
        "history": history,
        "forecast": forecast,
    }


def model_info() -> dict:
    return _META or {"status": "no model registered"}
