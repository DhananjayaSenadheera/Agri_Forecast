"""Harvest-price prediction logic.

Routes to the ML model when the gate promoted it; otherwise serves the
crop-mean fallback (current best predictor). Always returns an ordered
P10/P50/P90 interval and never crashes on unknown crops.
"""
from __future__ import annotations

from datetime import date, timedelta

import numpy as np
import pandas as pd
from sqlalchemy import text

from ..db import get_engine
from ..registry import registry
from . import explain

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
    sql = text("SELECT Name, GrowthPeriodDays FROM Crops WHERE Id = :cid")
    with get_engine().connect() as conn:
        df = pd.read_sql(sql, conn, params={"cid": crop_id})
    if not len(df):
        return None, None
    gp = df["GrowthPeriodDays"].iloc[0]
    return df["Name"].iloc[0], (int(gp) if pd.notna(gp) else None)


def _model_quantiles(row) -> dict:
    cols = _PAYLOAD["feature_cols"]
    X = pd.DataFrame([{c: row.get(c) for c in cols}])
    for c in cols:
        if c in _PAYLOAD["categorical"]:
            X[c] = X[c].astype("category")
        else:
            X[c] = pd.to_numeric(X[c], errors="coerce").astype("float64")
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
    model_active = bool(_PAYLOAD.get("beats_baseline"))

    top_factors: list[dict] = []
    if model_active and row is not None and gp:
        q = _model_quantiles(row)
        p10, p50, p90 = q["p10"], q["p50"], q["p90"]
        active, confidence = "model", "Medium"
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
    With the fallback active, the point forecast is flat (the crop's
    historical p50) and we are honest about it: the interval WIDENS with
    horizon (half-spread scaled by sqrt(horizon)) because a flat forecast is
    progressively less trustworthy further out.
    """
    if _PAYLOAD is None:
        raise RuntimeError("No model registered — run train_model_a.py first.")

    crop_id = str(crop_id).lower()  # normalize GUID case (prior bug)
    crop_name, _ = _crop_meta(crop_id)

    history = _monthly_history(crop_id, as_of, max_months=12)

    model_active = bool(_PAYLOAD.get("beats_baseline"))
    fb = _PAYLOAD["fallback"]
    per = fb["per_crop"].get(crop_id) or fb["global"]
    p10, p50, p90 = float(per["p10"]), float(per["p50"]), float(per["p90"])
    have_crop = crop_id in fb["per_crop"]

    if model_active:
        active, confidence = "model", "Medium"
        explanation = "ML model forecast from current price, season and recent weather."
    else:
        active = "crop_mean_fallback"
        confidence = "Medium" if have_crop else "Low"
        explanation = ("Based on this crop's historical harvest-price distribution. "
                       "(The ML model is not yet more accurate than this baseline at "
                       "current data volume, so the central forecast is flat; the band "
                       "widens with horizon to reflect growing uncertainty.)")

    # h=1 reproduces the fallback's actual [p10, p90] (so the 1-month band matches
    # what /predict returns for the same crop); each side's distance from the median
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
