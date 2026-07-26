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
    # IsFestival was removed from the feature store, but the promoted model still lists it
    # in feature_cols - keep the label until the next retrain drops it from the contract.
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
    # National news-sentiment signals.
    "MeanSentiment": "news sentiment",
    "DroughtRatio": "drought news coverage",
    "FloodRatio": "flood news coverage",
    "PolicyRatio": "policy news coverage",
    # Government-policy signals.
    "ActivePolicyNetDirection": "net effect of active policies",
    "ActivePolicyCount": "number of active policies",
    "PolicyImportBanActive": "import ban in effect",
    "PolicyPriceCeilingActive": "price ceiling in effect",
    "PolicyFertiliserSubsidyActive": "fertiliser subsidy in effect",
    # CBSL macro-series signals (as-of join on the publication date).
    "MacroFoodInflationYoY": "food inflation rate",
    "MacroFoodImportsYoY": "food import trend",
    "MacroPolicyRateOPR": "central bank interest rate",
    # Cross-market signals: point-in-time prices for the feature-safe markets plus derived
    # summaries. Named explicitly so SHAP shows a market name, not a raw column.
    "MktDambullaAvgPrice": "Dambulla market price",
    "MktDambullaLag7": "Dambulla price 7 days ago",
    "MktKeppetipolaAvgPrice": "Keppetipola market price",
    "MktKeppetipolaLag7": "Keppetipola price 7 days ago",
    "MktThambuttegamaAvgPrice": "Thambuttegama market price",
    "MktThambuttegamaLag7": "Thambuttegama price 7 days ago",
    "MktPettahAvgPrice": "Pettah market price",
    "MktPettahLag7": "Pettah price 7 days ago",
    "MktNarahenpitaAvgPrice": "Narahenpita market price",
    "MktNarahenpitaLag7": "Narahenpita price 7 days ago",
    # Markets added when the HARTI parser was widened. The slug is the first word of the
    # market name (load._market_slug), so 'Nuwara Eliya' becomes 'Nuwara'.
    "MktKandyAvgPrice": "Kandy market price",
    "MktKandyLag7": "Kandy price 7 days ago",
    "MktMeegodaAvgPrice": "Meegoda market price",
    "MktMeegodaLag7": "Meegoda price 7 days ago",
    "MktNorochcholeAvgPrice": "Norochchole market price",
    "MktNorochcholeLag7": "Norochchole price 7 days ago",
    "MktNuwaraAvgPrice": "Nuwara Eliya market price",
    "MktNuwaraLag7": "Nuwara Eliya price 7 days ago",
    "MktBandarawelaAvgPrice": "Bandarawela market price",
    "MktBandarawelaLag7": "Bandarawela price 7 days ago",
    "MktVeyangodaAvgPrice": "Veyangoda market price",
    "MktVeyangodaLag7": "Veyangoda price 7 days ago",
    "SpreadVsNational": "local price vs national average",
    "MarketRankPct": "local price rank across markets",
    "LeaderMarketLag7": "leading market price 7 days ago",
    "NMarketsReporting": "number of markets reporting",
}


def _label(col: str) -> str:
    return _LABELS.get(col, col)


def _shap_contributions(row, payload):
    """Per-feature SHAP contributions of the p50 prediction for one feature row.

    Returns (feature_cols, sv) where sv is a 1-D array of signed contributions aligned with
    feature_cols, or None on any unexpected shape. Shared by top_factors and
    top_factor_codes so the two can never drift.

    Explains the p50 model that actually produced the forecast. The per-crop residual
    offset is an additive log-space shift, so it changes neither SHAP signs nor ranking.
    The values are in log space, but only their sign and relative magnitude are surfaced.
    """
    if row is None or payload is None:
        return None
    if payload.get("served_ml_kind") == "residual" and "residual_models" in payload:
        model = payload["residual_models"]["p50"]
    else:
        model = payload["models"]["p50"]
    X = _build_X(row, payload)

    import shap

    explainer = shap.TreeExplainer(model)
    sv = np.asarray(explainer.shap_values(X)).reshape(-1)  # single row -> per-feature
    cols = payload["feature_cols"]
    if sv.shape[0] != len(cols):
        return None
    return cols, sv


def top_factors(row, payload, top_n: int = 5) -> list[dict]:
    """Top-N SHAP drivers of the p50 prediction for one feature row.

    Returns [{name, direction, weight}] sorted by descending |SHAP|. Legacy English-label
    surface; serving now emits top_factor_codes instead. Returns [] on any error.
    """
    try:
        contrib = _shap_contributions(row, payload)
        if contrib is None:
            return []
        cols, sv = contrib
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


# The trilingual FE renders factor labels from stable snake_case codes (i18n keys under
# factor.codes.*), never by parsing English prose. Raw feature columns are grouped into
# a handful of farmer-meaningful factors and the FE owns the translations. An unknown
# code renders as muted raw text, which is safe.
FACTOR_RECENT_PRICE_TREND = "recent_price_trend"
FACTOR_FESTIVAL_DEMAND = "festival_demand"
FACTOR_SEASONAL_SUPPLY = "seasonal_supply"
FACTOR_WEATHER_MONSOON = "weather_monsoon"
FACTOR_MARKET_CONDITIONS = "market_conditions"
FACTOR_ECONOMIC_CONDITIONS = "economic_conditions"

# Price levels / lags / rolling stats / momentum -> "recent price trend".
_PRICE_TREND_COLS = {
    "AvgPrice", "MaxPrice", "MinPrice",
    "RollMean7", "RollMean30", "RollMean90",
    "RollStd30", "RollStd90",
    "Lag7", "Lag30", "Lag60", "Lag90",
    "Momentum", "PctChange30", "PriceZScore90",
}
# Festival / national-calendar countdown features -> "festival demand".
_FESTIVAL_COLS = {
    "IsFestival",  # legacy column still in older promoted feature_cols
    "HarvestInFestivalLeadup", "DaysFromHarvestToNextFestival",
    "DaysToNextFestivalAny", "InLeadupAvurudu", "InLeadupChristmas",
}
# Month / season / day-of-year encodings -> "seasonal supply".
_SEASON_COLS = {
    "SeasonMaha", "PlantingSeasonEnc", "MonthNum",
    "DayOfYear", "WeekOfYear", "SinDoy", "CosDoy",
}
# Drought and flood news ratios are grouped with the measured Wx* weather columns:
# a farmer reads that coverage as weather.
_WEATHER_EXTRA_COLS = {"DroughtRatio", "FloodRatio"}
# Cross-market summaries, grouped with all the per-market Mkt* columns.
_MARKET_EXTRA_COLS = {
    "SpreadVsNational", "MarketRankPct", "LeaderMarketLag7", "NMarketsReporting",
}
# FX / CBSL macro / policy / news-sentiment -> "economic conditions".
_ECON_COLS = {
    "FxUsdLkr", "MeanSentiment", "PolicyRatio",
    "ActivePolicyNetDirection", "ActivePolicyCount",
    "PolicyImportBanActive", "PolicyPriceCeilingActive",
    "PolicyFertiliserSubsidyActive",
    "MacroFoodInflationYoY", "MacroFoodImportsYoY", "MacroPolicyRateOPR",
}
# Crop-static identity and agronomy columns are not actionable per forecast, so they
# are deliberately dropped from the factor breakdown.
_DROP_COLS = {"CropId", "GrowthPeriodDays", "HarvestWindowDays"}

# A factor whose share of the total absolute contribution is below this is reported
# as 'neutral' rather than up or down.
_NEUTRAL_SHARE = 0.01


def factor_code_for(col: str) -> str | None:
    """Map a raw model feature column to a factor code, or None if it is dropped.

    The Wx* and Mkt* prefix rules cover the weather and market families, so a new column
    in either maps without a code change.
    """
    if col in _DROP_COLS:
        return None
    if col in _PRICE_TREND_COLS:
        return FACTOR_RECENT_PRICE_TREND
    if col in _FESTIVAL_COLS:
        return FACTOR_FESTIVAL_DEMAND
    if col in _SEASON_COLS:
        return FACTOR_SEASONAL_SUPPLY
    if col.startswith("Wx") or col in _WEATHER_EXTRA_COLS:
        return FACTOR_WEATHER_MONSOON
    if col.startswith("Mkt") or col in _MARKET_EXTRA_COLS:
        return FACTOR_MARKET_CONDITIONS
    if col in _ECON_COLS:
        return FACTOR_ECONOMIC_CONDITIONS
    return None


def top_factor_codes(row, payload, top_n: int = 4) -> list[dict]:
    """Top-N farmer-meaningful factor codes driving the p50 prediction.

    Returns [{code, direction, weight}]: code is a stable snake_case i18n key, direction is
    the sign of the code's net SHAP contribution ('neutral' when its share is below
    _NEUTRAL_SHARE), and weight is the code's share of the total absolute contribution,
    which the FE renders as a percentage bar.

    Per-feature contributions are aggregated by code (several price lags collapse into one
    recent_price_trend), ranked by |net contribution| with ties broken on code name so the
    output is deterministic. Returns [] on any error.
    """
    try:
        contrib = _shap_contributions(row, payload)
        if contrib is None:
            return []
        cols, sv = contrib
        agg: dict[str, float] = {}
        for col, val in zip(cols, sv):
            code = factor_code_for(col)
            if code is None:
                continue
            agg[code] = agg.get(code, 0.0) + float(val)
        total = sum(abs(v) for v in agg.values())
        if not agg or total <= 0.0:
            return []
        # Descending |net contribution|; deterministic tie-break on code name.
        ranked = sorted(agg.items(), key=lambda kv: (-abs(kv[1]), kv[0]))[:top_n]
        out: list[dict] = []
        for code, val in ranked:
            share = abs(val) / total
            direction = "neutral" if share < _NEUTRAL_SHARE else ("up" if val > 0 else "down")
            out.append({"code": code, "direction": direction, "weight": round(share, 3)})
        return out
    except Exception:
        return []
