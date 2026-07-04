"""SHAP top-factor explanations for a single harvest-price prediction.

Uses TreeExplainer on the p50 quantile model. Returns farmer-readable
top-N drivers as [{name, direction, weight}]. Never raises: any failure
(missing model, shap error, bad row) yields an empty list so the caller
can fall back to the static explanation.
"""
from __future__ import annotations

import numpy as np

from .build_x import build_x as _build_X  # single source of truth (shared with predict.py)

# Raw CropFeatureDaily column -> farmer-readable label.
_LABELS = {
    "AvgPrice": "current market price",
    "MaxPrice": "recent high price",
    "MinPrice": "recent low price",
    "RollMean7": "recent 7-day price trend",
    "RollMean30": "recent 30-day price trend",
    "RollMean90": "recent 90-day price trend",
    "RollStd30": "recent 30-day price volatility",
    "RollStd90": "recent 90-day price volatility",
    "Lag7": "price 7 days ago",
    "Lag30": "price 30 days ago",
    "Lag60": "price 60 days ago",
    "Lag90": "price 90 days ago",
    "Momentum": "price momentum",
    "PctChange30": "30-day price change",
    "PriceZScore90": "price vs its 90-day norm",
    "SeasonMaha": "Maha season",
    "PlantingSeasonEnc": "planting season",
    # Legacy: the old hardcoded IsFestival column was removed from the feature
    # store (R1.1 P2), but the CURRENTLY-PROMOTED model was trained with it in
    # its feature_cols. Keep this label so live SHAP does not regress to a raw
    # column name until the next retrain drops IsFestival from the contract.
    "IsFestival": "festival demand",
    # Festival demand (from the national festival calendar).
    "HarvestInFestivalLeadup": "harvest lands in a festival season",
    "DaysFromHarvestToNextFestival": "days from harvest to the next festival",
    "DaysToNextFestivalAny": "days until the next festival",
    "InLeadupAvurudu": "Avurudu (New Year) demand",
    "InLeadupChristmas": "Christmas demand",
    "MonthNum": "time of year",
    "DayOfYear": "time of year",
    "WeekOfYear": "time of year",
    "SinDoy": "seasonal cycle",
    "CosDoy": "seasonal cycle",
    "WxRainfallMm": "recent rainfall",
    "WxRainLag1": "rainfall last month",
    "WxRainLag2": "rainfall two months ago",
    "WxRainAnomaly": "rainfall vs normal",
    "WxAvgTempC": "recent temperature",
    "WxTempLag1": "temperature last month",
    "WxTempLag2": "temperature two months ago",
    "GrowthPeriodDays": "crop growth period",
    "HarvestWindowDays": "harvest window length",
    "CropId": "crop type",
    # National FX signal (point-in-time as-of join).
    "FxUsdLkr": "US dollar exchange rate",
    # National news-sentiment signals (backfilled latent debt, 2026-07-04).
    "MeanSentiment": "news sentiment",
    "DroughtRatio": "drought news coverage",
    "FloodRatio": "flood news coverage",
    "PolicyRatio": "policy news coverage",
    # Government-policy signals (backfilled latent debt, 2026-07-04).
    "ActivePolicyNetDirection": "net effect of active policies",
    "ActivePolicyCount": "number of active policies",
    "PolicyImportBanActive": "import ban in effect",
    "PolicyPriceCeilingActive": "price ceiling in effect",
    "PolicyFertiliserSubsidyActive": "fertiliser subsidy in effect",
    # CBSL macro-series signals (P3, vintage as-of join on publication date).
    "MacroFoodInflationYoY": "food inflation rate",
    "MacroFoodImportsYoY": "food import trend",
    "MacroPolicyRateOPR": "central bank interest rate",
}


def _label(col: str) -> str:
    return _LABELS.get(col, col)


def top_factors(row, payload, top_n: int = 5) -> list[dict]:
    """Top-N SHAP drivers of the p50 prediction for one feature row.

    Returns [{name: str, direction: "up"|"down", weight: float}] sorted by
    descending |SHAP|. The p50 model is trained on the log1p target; SHAP
    values are in log-space but their SIGN and relative MAGNITUDE (what pushed
    the price up vs down, and by how much) are what we surface, so log-space is
    fine for ranking and direction. Returns [] on any error.
    """
    try:
        if row is None or payload is None:
            return []
        # Explain the p50 model that actually produced the forecast: the residual
        # p50 model when the residual path is served, else the pooled p50. The
        # per-crop offset is an additive log-space shift, so it does NOT change
        # SHAP signs/ranking of the feature drivers — explaining the residual
        # model's drivers is correct for the residual prediction.
        if payload.get("served_ml_kind") == "residual" and "residual_models" in payload:
            model = payload["residual_models"]["p50"]
        else:
            model = payload["models"]["p50"]
        X = _build_X(row, payload)

        import shap

        explainer = shap.TreeExplainer(model)
        sv = explainer.shap_values(X)
        sv = np.asarray(sv).reshape(-1)  # single row -> per-feature contributions
        cols = payload["feature_cols"]
        if sv.shape[0] != len(cols):
            return []

        order = np.argsort(np.abs(sv))[::-1][:top_n]
        out: list[dict] = []
        for i in order:
            val = float(sv[i])
            if val == 0.0:
                continue
            out.append({
                "name": _label(cols[i]),
                "direction": "up" if val > 0 else "down",
                "weight": round(abs(val), 4),
            })
        return out
    except Exception:
        return []
