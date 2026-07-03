"""Load raw inputs from SQL Server into pandas DataFrames."""
from __future__ import annotations

import pandas as pd

from .db import get_engine


def load_prices() -> pd.DataFrame:
    # SPLICE / DEDUP RULE (enforced here, not in the DB):
    #
    # HARTI is the preferred source for pre-DEC history.  DEC is authoritative
    # from 2025-05-05 onward.  However, for Ridge Gourd and Beans, DEC data in
    # the window 2025-05-05 -> 2025-06-30 is garbage (CV 49-58% in the overlap
    # spike -- noisy DEC launch period).  For those two crops we use HARTI
    # through 2025-06-30 and exclude DEC in that window.
    #
    # The HARTI loader already only inserted HARTI rows for:
    #   - All 6 crops:        PriceDate < 2025-05-05
    #   - Ridge Gourd/Beans:  PriceDate <= 2025-06-30  (exception)
    #
    # This WHERE clause excludes the noisy DEC rows for those two crops so that
    # no (CropId, PriceDate) reaches the feature build from both sources.
    # DEC rows are NOT deleted from the DB -- this filter is the only gate.
    sql = """
        SELECT mp.CropId, c.CropCode, c.Name AS CropName,
               mp.PriceDate, mp.MinPrice, mp.MaxPrice
        FROM MarketPrices mp
        JOIN Crops c ON c.Id = mp.CropId
        WHERE mp.CropId IS NOT NULL AND mp.MaxPrice > 0
          AND NOT (
            -- Exclude noisy DEC rows for Ridge Gourd + Beans in DEC-launch window.
            -- HARTI rows for these crops in [2025-05-05, 2025-06-30] are preferred.
            mp.Source = 'DAMBULLA_DEC'
            AND mp.PriceDate >= '2025-05-05'
            AND mp.PriceDate <= '2025-06-30'
            AND c.Name IN ('Ridge Gourd', 'Beans')
          )
    """
    df = pd.read_sql(sql, get_engine())
    df["CropId"] = df["CropId"].astype(str)
    df["PriceDate"] = pd.to_datetime(df["PriceDate"])
    for col in ("MinPrice", "MaxPrice"):
        df[col] = df[col].astype(float)
    df["AvgPrice"] = (df["MinPrice"] + df["MaxPrice"]) / 2.0
    return df


def load_crops() -> pd.DataFrame:
    sql = """
        SELECT Id AS CropId, CropCode, Name AS CropName,
               GrowthPeriodDays, PlantingSeason, HarvestWindowDays
        FROM Crops
    """
    df = pd.read_sql(sql, get_engine())
    df["CropId"] = df["CropId"].astype(str)
    return df


def load_fx() -> pd.DataFrame:
    """USD->LKR exchange-rate series for a point-in-time (as-of) join.

    open.er-api.com is latest-only, so historical FX is not backfilled --
    expect this to be sparse (one row today). Returns [date, fx_usd_lkr]
    sorted ascending so it can be consumed by pd.merge_asof.
    """
    sql = "SELECT Date, Value FROM EconomicIndicators WHERE IndicatorCode = 'USD_LKR'"
    df = pd.read_sql(sql, get_engine())
    df["date"] = pd.to_datetime(df["Date"])
    df["fx_usd_lkr"] = df["Value"].astype(float)
    return df[["date", "fx_usd_lkr"]].sort_values("date").reset_index(drop=True)


def load_weather() -> pd.DataFrame:
    sql = "SELECT Month, AvgTemperature, TotalRainfall FROM WeatherRecords"
    df = pd.read_sql(sql, get_engine())
    df["Month"] = pd.to_datetime(df["Month"]).dt.to_period("M")
    for col in ("AvgTemperature", "TotalRainfall"):
        df[col] = df[col].astype(float)
    return df.sort_values("Month").reset_index(drop=True)


# Empty-frame schemas so a missing/empty source degrades to all-NaN features
# (mirrors load_fx returning a sparse series) rather than failing the build.
_POLICY_COLS = ["Id", "PolicyType", "Title", "EffectiveFrom", "EffectiveTo",
                "Direction", "Source", "ReferenceUrl"]
_SENTIMENT_COLS = ["Date", "MeanSentiment", "ArticleCount",
                   "DroughtRatio", "FloodRatio", "PolicyRatio"]
_FESTIVAL_COLS = ["FestivalKey", "Date", "LeadUpDays", "IsProvisional", "Source"]


def load_policy_flags() -> pd.DataFrame:
    """Government-policy flags as date ranges for a point-in-time (active-as-of)
    join.

    A flag is active on date D iff EffectiveFrom <= D AND (EffectiveTo IS NULL OR
    D <= EffectiveTo). Knowledge date = EffectiveFrom (the policy is publicly in
    effect from then), so attaching it at D is leakage-safe. CreatedAtUtc is an
    audit field and is deliberately NOT loaded -- it must never become a feature.

    Returns columns [Id, PolicyType, Title, EffectiveFrom, EffectiveTo,
    Direction, Source, ReferenceUrl] with EffectiveFrom/EffectiveTo as
    datetimes (EffectiveTo NaT = still active), sorted by EffectiveFrom.
    """
    sql = """
        SELECT Id, PolicyType, Title, EffectiveFrom, EffectiveTo,
               Direction, Source, ReferenceUrl
        FROM PolicyFlags
    """
    try:
        df = pd.read_sql(sql, get_engine())
    except Exception:
        return pd.DataFrame(columns=_POLICY_COLS)
    if df.empty:
        return pd.DataFrame(columns=_POLICY_COLS)
    df["EffectiveFrom"] = pd.to_datetime(df["EffectiveFrom"])
    df["EffectiveTo"] = pd.to_datetime(df["EffectiveTo"])  # NULL -> NaT
    df["PolicyType"] = df["PolicyType"].astype(int)
    df["Direction"] = df["Direction"].astype(int)
    return df.sort_values("EffectiveFrom").reset_index(drop=True)


def load_festivals() -> pd.DataFrame:
    """National festival calendar as point-in-time date rows for the feature build.

    Reads FestivalCalendarEntries -- the SINGLE SOURCE OF TRUTH for festival dates
    (mirrors load_policy_flags()/load_fx(): same engine, try/except -> empty-frame
    degrade so a missing/empty table attaches all-zero festival features rather than
    failing the build). There is deliberately NO static festival_days.py twin.

    ASYMMETRY WITH POYA (deliberate): the Poya calendar stays Python-static
    (agriforecast_ml/data/poya_days.py) because its ONLY consumer is data-quality
    gap-suppression (is_poya / expected_market_closed) -- a QA concern that must run
    with no DB and is not a model feature. Festivals ARE model features that as-of-
    join against price history, so they live in the DB where the .NET seed/gazette-
    update path owns them. Two consumers, two lifecycles => two homes.

    Date is stored date-only (no time) so it can never carry a hidden "now" leakage.
    CreatedAtUtc is an audit field and is deliberately NOT loaded -- it must never
    become a feature. IsProvisional is passed through untouched (never upgraded).
    LeadUpDays comes straight from the row so the Avurudu Apr-13-anchored / Apr-14=0
    window convention is preserved without Python re-deriving it.

    Returns columns [FestivalKey, Date, LeadUpDays, IsProvisional, Source] with Date
    as datetime, sorted by Date ascending (ready for merge_asof and for
    to_prophet_holidays()).
    """
    sql = """
        SELECT FestivalKey, Date, LeadUpDays, IsProvisional, Source
        FROM FestivalCalendarEntries
    """
    try:
        df = pd.read_sql(sql, get_engine())
    except Exception:
        return pd.DataFrame(columns=_FESTIVAL_COLS)
    if df.empty:
        return pd.DataFrame(columns=_FESTIVAL_COLS)
    df["FestivalKey"] = df["FestivalKey"].astype(str)
    df["Date"] = pd.to_datetime(df["Date"])
    df["LeadUpDays"] = df["LeadUpDays"].astype(int)
    df["IsProvisional"] = df["IsProvisional"].astype(bool)
    return df.sort_values("Date").reset_index(drop=True)


def to_prophet_holidays(festivals: pd.DataFrame) -> pd.DataFrame:
    """Reshape load_festivals() output into a Prophet-native holidays frame.

    Pure calendar function (no DB, no wall-clock). Prophet expects
    [holiday, ds, lower_window, upper_window] where the effect spans
    [ds + lower_window, ds + upper_window]. Our lead-up window is
    [Date - LeadUpDays, Date], so lower_window = -LeadUpDays and upper_window = 0.

    This helper exists purely to prove Prophet-readiness of the calendar; NO
    Prophet integration is wired here (XGBoost Model A is the only live consumer).

    Empty in -> empty out with the right columns.
    """
    cols = ["holiday", "ds", "lower_window", "upper_window"]
    if festivals is None or festivals.empty:
        return pd.DataFrame(columns=cols)
    out = pd.DataFrame({
        "holiday": festivals["FestivalKey"].astype(str).values,
        "ds": pd.to_datetime(festivals["Date"]).values,
        "lower_window": -festivals["LeadUpDays"].astype(int).values,
        "upper_window": 0,
    })
    return out[cols].reset_index(drop=True)


def load_news_sentiment() -> pd.DataFrame:
    """National daily news-sentiment signal for a point-in-time (as-of) join.

    NewsSentimentDaily is a Python-owned ML table (built by score_news.py). The
    live news fetch may not have run yet, so the table can be MISSING or EMPTY --
    in that case we return an empty frame with the right columns so the sentiment
    features attach as all-NaN (exactly like load_fx with a sparse FX series). No
    article means a date is ABSENT, so the backward as-of join carries the last
    known reading forward without ever using a Date after the observation date.

    Returns [Date, MeanSentiment, ArticleCount, DroughtRatio, FloodRatio,
    PolicyRatio] sorted by Date ascending.
    """
    sql = """
        SELECT Date, MeanSentiment, ArticleCount,
               DroughtRatio, FloodRatio, PolicyRatio
        FROM NewsSentimentDaily
    """
    try:
        df = pd.read_sql(sql, get_engine())
    except Exception:
        return pd.DataFrame(columns=_SENTIMENT_COLS)
    if df.empty:
        return pd.DataFrame(columns=_SENTIMENT_COLS)
    df["Date"] = pd.to_datetime(df["Date"])
    for col in ("MeanSentiment", "DroughtRatio", "FloodRatio", "PolicyRatio"):
        df[col] = df[col].astype(float)
    df["ArticleCount"] = df["ArticleCount"].astype(int)
    return df.sort_values("Date").reset_index(drop=True)
