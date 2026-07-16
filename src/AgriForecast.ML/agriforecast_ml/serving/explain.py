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
    # Cross-market spread signals (P4 step 2, ClickUp 86caheffr). Per-market
    # point-in-time prices for the feature-safe markets + derived cross-market
    # summaries. The 5 feature-safe markets are named explicitly so SHAP shows a
    # farmer a market name, never a raw Mkt<Slug>AvgPrice column.
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
    # 6 markets added when the HARTI parser was widened (R2 Step 6): Kandy,
    # Meegoda, Norochchole, Nuwara Eliya, Bandarawela, Veyangoda. Slug = first
    # word of the market Name (load._market_slug), so 'Nuwara Eliya' -> 'Nuwara'.
    # These enter the feature contract at the R2 Step 7 rebuild+retrain.
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

    Returns ``(feature_cols, sv)`` where ``sv`` is a 1-D numpy array of signed
    per-feature contributions aligned with ``feature_cols``, or ``None`` on any
    unexpected shape. Shared by ``top_factors`` (legacy label output) and
    ``top_factor_codes`` (API-5 factor codes) so the two can never drift.

    Explains the p50 model that actually produced the forecast: the residual p50
    model when the residual path is served, else the pooled p50. The per-crop
    offset is an additive log-space shift, so it does NOT change SHAP signs or
    ranking of the feature drivers. SHAP values are in log-space (log1p target),
    but their SIGN and relative MAGNITUDE are what we surface, so log-space is
    fine for ranking and direction. Deterministic for a fixed model + input.
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

    Returns [{name: str, direction: "up"|"down", weight: float}] sorted by
    descending |SHAP|. LEGACY (English-label) surface, retained for backward
    compatibility; serving now emits `top_factor_codes` instead. Returns [] on
    any error.
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


# ===========================================================================
# API-5: machine-stable, translatable FACTOR CODES
# ===========================================================================
# The trilingual FE renders farmer-facing factor labels from stable snake_case
# CODES (i18n keys under `factor.codes.*`), never by parsing English prose. Raw
# model feature columns are grouped into a handful of farmer-meaningful factors;
# the FE owns the Sinhala/Tamil renderings.
#
# FE i18n coverage (ForecastUI src/i18n/locales/*/translation `factor.codes`):
#   recent_price_trend, festival_demand, seasonal_supply, weather_monsoon  -> KNOWN
#   market_conditions, economic_conditions                                 -> NEW
# Unknown codes render as muted raw text on the FE (safe); the two NEW codes
# are flagged in the API-5 report so the i18n files gain entries.
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
# Weather-EVENT news ratios grouped with measured weather (all `Wx*` cols) ->
# "weather and monsoon" (a farmer reads drought/flood coverage as weather).
_WEATHER_EXTRA_COLS = {"DroughtRatio", "FloodRatio"}
# Cross-market summaries grouped with all per-market `Mkt*` cols -> "market
# conditions".
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
# Crop-STATIC identity / agronomy columns: not an actionable per-forecast factor
# (CropId is the crop's baseline level, growth/harvest windows are fixed agronomy)
# -> intentionally DROPPED from the factor breakdown.
_DROP_COLS = {"CropId", "GrowthPeriodDays", "HarvestWindowDays"}

# A factor whose share of the total absolute contribution is below this is
# reported as 'neutral' rather than up/down (near-zero net push).
_NEUTRAL_SHARE = 0.01


def factor_code_for(col: str) -> str | None:
    """Map a raw model feature column to a farmer-meaningful factor CODE, or
    None if the column is intentionally dropped / unmapped (drop from breakdown).
    Prefix rules (`Wx*`, `Mkt*`) cover the point-in-time market/weather families
    so a future column in those families maps without a code change."""
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
    """Top-N farmer-meaningful factor CODES driving the p50 prediction.

    Returns [{code: str, direction: "up"|"down"|"neutral", weight: float}]:
      * code      — stable snake_case i18n key (see `factor_code_for`).
      * direction — sign of the code's NET (summed) SHAP contribution; 'neutral'
                    when its share of total attribution is below `_NEUTRAL_SHARE`.
      * weight    — the code's share (0..1, rounded 3dp) of the total absolute
                    contribution across all MAPPED codes (a relative strength the
                    FE renders as a percentage bar).

    Per-feature SHAP contributions are aggregated by factor code (multiple price
    lags collapse into one `recent_price_trend`, etc.), ranked by |net
    contribution|, ties broken by code name so the output is deterministic for a
    fixed model + input. Returns [] on any error (caller falls back to prose).
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
