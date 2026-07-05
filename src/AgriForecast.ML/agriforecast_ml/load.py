"""Load raw inputs from SQL Server into pandas DataFrames."""
from __future__ import annotations

import pandas as pd

from .db import get_engine

# Canonical datetime resolution for EVERY datetime column that leaves this module.
#
# pandas 3.0 made pd.merge_asof strict about join-key dtype resolution, and
# pd.read_sql now returns datetime columns at whatever unit the driver reports
# (datetime64[s], datetime64[us], ...). pd.to_datetime PRESERVES that unit, so a
# price series read as [s] and an FX series read as [us] have incompatible
# datetime64 dtypes and the as-of merges in features.py raise
# "incompatible merge keys ... must be the same type".
#
# Every loader normalizes its datetime columns to this single unit so all
# downstream consumers (as-of merges, period conversions, festival windows) see
# consistent dtypes. This is a PURE dtype normalization: same wall-clock dates,
# same values, no timezone/logic change (ns has more than enough range for the
# 2015+ agricultural date domain).
_CANON_DT = "datetime64[ns]"


def _as_canon_dt(s: pd.Series) -> pd.Series:
    """Parse to datetime and pin the resolution to _CANON_DT (leakage-inert)."""
    return pd.to_datetime(s).astype(_CANON_DT)


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
    # PriceDate is the SOURCE of ObservationDate/HarvestDate downstream, so pin it
    # to the canonical unit here — this is where the as-of merge's LEFT key dtype
    # is decided.
    df["PriceDate"] = _as_canon_dt(df["PriceDate"])
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


_CROP_CATEGORY_COLS = ["crop_id", "category_id", "category_code",
                       "category_name", "parent_code"]


def load_crop_categories() -> pd.DataFrame:
    """Crop taxonomy: one row per crop with its category (and parent category).

    The DB ``CropCategories`` table is the SOURCE OF TRUTH for crop taxonomy
    (R2 Step 1). Consumers must NOT hardcode category maps in Python -- read this
    loader (or the DB table) instead. The retired 11-GUID / 4-family static maps
    are dead; do not resurrect them.

    Joins Crops -> CropCategories -> parent CropCategories (LEFT, so a top-level
    category has ``parent_code = None``). ``Crops.CropCategoryId`` is FK-backfilled
    for all live crops; a crop whose category is somehow unmapped is dropped by the
    inner join (it has no taxonomy to serve).

    Mirrors load_policy_flags()/load_fx(): same engine, try/except -> typed
    empty-frame degrade so a missing/empty CropCategories table yields an empty
    taxonomy frame rather than failing the caller. GUID columns are lowercased so
    the .NET<->Python boundary never misses on case (uppercase-GUID fallback miss
    was a real bug).

    Returns [crop_id, category_id, category_code, category_name, parent_code]:
    crop_id/category_id lowercase str GUIDs, parent_code None for top-level
    categories.
    """
    sql = """
        SELECT c.Id           AS crop_id,
               cat.Id         AS category_id,
               cat.Code       AS category_code,
               cat.Name       AS category_name,
               parent.Code    AS parent_code
        FROM Crops c
        JOIN CropCategories cat ON cat.Id = c.CropCategoryId
        LEFT JOIN CropCategories parent ON parent.Id = cat.ParentId
    """
    try:
        df = pd.read_sql(sql, get_engine())
    except Exception:
        return pd.DataFrame(columns=_CROP_CATEGORY_COLS)
    if df.empty:
        return pd.DataFrame(columns=_CROP_CATEGORY_COLS)
    # Lowercase the GUID columns (the .NET boundary emits mixed case).
    df["crop_id"] = df["crop_id"].astype(str).str.lower()
    df["category_id"] = df["category_id"].astype(str).str.lower()
    df["category_code"] = df["category_code"].astype(str)
    df["category_name"] = df["category_name"].astype(str)
    # parent_code is NULL for top-level categories -> keep it as None, not "None".
    df["parent_code"] = df["parent_code"].where(df["parent_code"].notna(), None)
    return df.reset_index(drop=True)


def load_fx() -> pd.DataFrame:
    """USD->LKR exchange-rate series for a point-in-time (as-of) join.

    open.er-api.com is latest-only, so historical FX is not backfilled --
    expect this to be sparse (one row today). Returns [date, fx_usd_lkr]
    sorted ascending so it can be consumed by pd.merge_asof.
    """
    sql = "SELECT Date, Value FROM EconomicIndicators WHERE IndicatorCode = 'USD_LKR'"
    df = pd.read_sql(sql, get_engine())
    df["date"] = _as_canon_dt(df["Date"])
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
_MACRO_COLS = ["SeriesCode", "ReferenceDate", "PublishedAt", "Value",
               "IsPublishedAtImputed"]


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
    df["EffectiveFrom"] = _as_canon_dt(df["EffectiveFrom"])
    df["EffectiveTo"] = _as_canon_dt(df["EffectiveTo"])  # NULL -> NaT
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
    df["Date"] = _as_canon_dt(df["Date"])
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


def load_macro_series() -> pd.DataFrame:
    """CBSL macro-series vintages (MacroSeriesPoints) for a point-in-time join.

    Mirrors load_policy_flags()/load_fx()/load_news_sentiment(): same engine,
    try/except -> empty-frame degrade so a missing/empty table attaches all-NaN
    macro features rather than failing the build.

    EACH ROW CARRIES TWO DATES:
      - PublishedAt   = the vintage / KnowledgeDate: the first date the world
                        could know this value. This is the ONLY leakage-safe
                        join key (features.py _attach_macro merges as-of on it).
      - ReferenceDate = the period the value describes (audit-only). A monthly
                        index published weeks after its reference month is a
                        classic lookahead trap if joined on ReferenceDate, so
                        _attach_macro NEVER joins on it and drops it before the
                        model frame. It is loaded here only for provenance.

    IsPublishedAtImputed flags vintages whose PublishedAt was imputed from a
    per-series publication-lag prior (real release date not scrapeable). Loaded
    for audit; not itself a feature.

    Both date columns are pinned to the canonical [ns] unit (like every other
    loader) so the downstream as-of merge sees a consistent datetime64 dtype.
    RetrievedAtUtc / Source are deliberately NOT loaded -- audit fields that must
    never become features.

    Returns [SeriesCode, ReferenceDate, PublishedAt, Value, IsPublishedAtImputed]
    sorted by (SeriesCode, PublishedAt) ascending -- ready for a per-series
    backward merge_asof on PublishedAt.
    """
    sql = """
        SELECT SeriesCode, ReferenceDate, PublishedAt, Value, IsPublishedAtImputed
        FROM MacroSeriesPoints
    """
    try:
        df = pd.read_sql(sql, get_engine())
    except Exception:
        return pd.DataFrame(columns=_MACRO_COLS)
    if df.empty:
        return pd.DataFrame(columns=_MACRO_COLS)
    df["SeriesCode"] = df["SeriesCode"].astype(str)
    # BOTH date columns pinned to [ns] (the [ns] invariant): PublishedAt is the
    # join key, ReferenceDate travels alongside for the two-date tripwire tests.
    df["ReferenceDate"] = _as_canon_dt(df["ReferenceDate"])
    df["PublishedAt"] = _as_canon_dt(df["PublishedAt"])
    df["Value"] = df["Value"].astype(float)
    df["IsPublishedAtImputed"] = df["IsPublishedAtImputed"].astype(bool)
    return df.sort_values(["SeriesCode", "PublishedAt"]).reset_index(drop=True)


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
    df["Date"] = _as_canon_dt(df["Date"])
    for col in ("MeanSentiment", "DroughtRatio", "FloodRatio", "PolicyRatio"):
        df[col] = df[col].astype(float)
    df["ArticleCount"] = df["ArticleCount"].astype(int)
    return df.sort_values("Date").reset_index(drop=True)
