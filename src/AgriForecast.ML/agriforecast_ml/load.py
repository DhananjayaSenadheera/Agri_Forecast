"""Load raw inputs from SQL Server into pandas DataFrames."""
from __future__ import annotations

import re

import pandas as pd

from .db import get_engine

# Every datetime column leaving this module is pinned to one unit. pd.read_sql returns
# whatever unit the driver reports ([s], [us], ...) and merge_asof in features.py raises
# if two join keys differ. Pure dtype normalisation: same wall-clock dates.
_CANON_DT = "datetime64[ns]"


def _as_canon_dt(s: pd.Series) -> pd.Series:
    """Parse to datetime and pin the resolution to _CANON_DT (leakage-inert)."""
    return pd.to_datetime(s).astype(_CANON_DT)


def _is_verified(is_verified) -> bool:
    """True if the agronomy profile is owner-verified (IsVerified == 1).

    NA-safe: a missing profile, a Python False, a numpy bool or a 0/1 int all give the
    right answer without raising on pd.NA. Anything not truthy-True is not verified.
    """
    if is_verified is None or is_verified is pd.NA:
        return False
    try:
        if pd.isna(is_verified):
            return False
    except (TypeError, ValueError):
        pass
    return bool(is_verified) is True


def resolve_forecast_gp(is_verified, growth_period_days) -> int | None:
    """Return the growth period a crop may be forecast with, or None.

    Single source of truth for whether a crop gets an ML harvest horizon: its
    CropAgronomyProfiles row must be owner-verified AND still carry a usable
    GrowthPeriodDays. An unverified profile is excluded exactly like a NULL growth
    period, and the caller degrades to the crop-mean fallback with no horizon. The
    feature build and serving both call this, so train and serve cannot drift apart.
    """
    if not _is_verified(is_verified):
        return None
    if growth_period_days is None or pd.isna(growth_period_days):
        return None
    gp = int(growth_period_days)
    # gp <= 0 makes price.shift(-gp) a degenerate label, so treat it as unforecastable.
    if gp <= 0:
        return None
    return gp


def load_prices() -> pd.DataFrame:
    # Source splice: HARTI is preferred for pre-DEC history and DEC is authoritative from
    # 2025-05-05. Ridge Gourd and Beans are the exception - DEC prices are too noisy in
    # its launch window, so HARTI is used through 2025-06-30 for those two crops. The
    # HARTI loader only inserted rows inside those windows and this WHERE clause drops
    # the overlapping DEC rows, so no (CropId, PriceDate) reaches the build from both
    # sources. Nothing is deleted from the DB; this filter is the only gate.
    sql = """
        SELECT mp.CropId, c.CropCode, c.Name AS CropName, mp.Source,
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
    # PriceDate becomes ObservationDate/HarvestDate downstream, so the as-of merge's
    # left-key dtype is decided here.
    df["PriceDate"] = _as_canon_dt(df["PriceDate"])
    for col in ("MinPrice", "MaxPrice"):
        df[col] = df[col].astype(float)
    # One crop can have several MarketPrices rows for the same (Source, PriceDate) when
    # the feed lists it under more than one external id (Passion has two Dambulla ids).
    # The per-crop feature build reindexes onto a unique daily grid and would raise on
    # duplicate labels, so collapse to one value per (CropId, Source, PriceDate).
    # Grouping inside Source keeps the HARTI/DEC splice above intact, and dropna=False
    # stops a NULL key silently dropping the row.
    df = (df.groupby(["CropId", "CropCode", "CropName", "Source", "PriceDate"],
                     as_index=False, sort=True, dropna=False)[["MinPrice", "MaxPrice"]]
            .mean())
    df = df.drop(columns=["Source"])
    df["AvgPrice"] = (df["MinPrice"] + df["MaxPrice"]) / 2.0
    return df


# Empty-frame schema so a missing PriceObservations table degrades to all-NaN spread
# features instead of failing the build.
_PRICE_OBS_COLS = ["MarketSlug", "CropId", "ObservedDate", "AvgPrice"]


def _market_slug(name: str) -> str:
    """Stable, readable column token for a market Name.

    'Dambulla Dedicated Economic Centre' -> 'Dambulla'. Takes the first word, stripped
    of non-alphanumerics, so column names like MktDambullaAvgPrice stay stable across
    DB row-order changes.
    """
    first = (name or "").strip().split()[0] if (name or "").strip() else ""
    return re.sub(r"[^0-9A-Za-z]", "", first)


# HARTI is the only source that has ever fed the spread features. The DEC mirror
# additively writes DAMBULLA_DEC rows at the same Dambulla market, so a
# source-agnostic aggregation here would silently blend two sources' prices.
# Widening this to DEC is a deliberate decision plus a retrain, never a side effect.
_MARKET_SPREAD_SOURCE = "HARTI"


def load_price_observations(*, source: str = _MARKET_SPREAD_SOURCE) -> pd.DataFrame:
    """Per-(market, crop, date) confirmed prices for the cross-market spread features.

    Reads PriceObservations only, never mixed row-by-row with MarketPrices (the two
    tables disagree on 12.6% of exact matches). The harvest-price label still comes
    from load_prices(); this loader only feeds point-in-time spread context.

    Filters: feature-safe markets only (no NationalAggregate double-count, no ECOMAP
    demo twins), IsUnitConfirmed = 1 for the fail-closed unit quarantine, MaxPrice > 0
    and CropId NOT NULL, and a single Source (default HARTI) so the training frame
    stays exactly what it has always read - widening it to DEC needs a deliberate
    decision and a retrain, not a silent change.

    AvgPrice is the (Min + Max) / 2 midpoint load_prices() also uses, averaged over any
    duplicate rows. Returns a long frame [MarketSlug, CropId, ObservedDate, AvgPrice]
    sorted ready for a per-market backward merge_asof; an empty frame if the source is
    missing or unreachable.
    """
    from .canonical import get_feature_safe_market_ids

    try:
        engine = get_engine()
        safe_ids = get_feature_safe_market_ids(engine)
    except Exception:
        return pd.DataFrame(columns=_PRICE_OBS_COLS)
    if not safe_ids:
        return pd.DataFrame(columns=_PRICE_OBS_COLS)

    # Cast to str so the driver binds the GUIDs the same way whatever dtype they are.
    id_list = [str(i) for i in safe_ids]
    placeholders = ", ".join(f":m{i}" for i in range(len(id_list)))
    params = {f"m{i}": v for i, v in enumerate(id_list)}
    params["source"] = source
    source_filter = "AND po.Source = :source" if source is not None else ""
    sql = f"""
        SELECT m.Name AS MarketName, po.CropId, po.ObservedDate,
               po.MinPrice, po.MaxPrice
        FROM PriceObservations po
        JOIN Markets m ON m.Id = po.MarketId
        WHERE po.MarketId IN ({placeholders})
          AND po.IsUnitConfirmed = 1
          AND po.MaxPrice > 0
          AND po.CropId IS NOT NULL
          {source_filter}
    """
    try:
        import sqlalchemy as sa
        df = pd.read_sql(sa.text(sql), engine, params=params)
    except Exception:
        return pd.DataFrame(columns=_PRICE_OBS_COLS)
    if df.empty:
        return pd.DataFrame(columns=_PRICE_OBS_COLS)

    df["MarketSlug"] = df["MarketName"].map(_market_slug)
    df["CropId"] = df["CropId"].astype(str)
    df["ObservedDate"] = _as_canon_dt(df["ObservedDate"])
    for col in ("MinPrice", "MaxPrice"):
        df[col] = df[col].astype(float)
    df["AvgPrice"] = (df["MinPrice"] + df["MaxPrice"]) / 2.0
    # Collapse any duplicate (market, crop, date) rows to one daily value.
    out = (df.groupby(["MarketSlug", "CropId", "ObservedDate"], as_index=False)
             ["AvgPrice"].mean())
    return out.sort_values(["MarketSlug", "CropId", "ObservedDate"]).reset_index(drop=True)


def feature_safe_market_slugs() -> list[str]:
    """The stable, sorted MarketSlug set for the feature-safe markets.

    Decides which per-market spread columns are emitted, so a market with no rows today
    still gets an explicit all-NaN column instead of silently disappearing. Sorted by
    MarketCode for deterministic column order; empty when the DB is unreachable.
    """
    try:
        import sqlalchemy as sa
        from .canonical import get_feature_safe_market_ids

        engine = get_engine()
        safe_ids = get_feature_safe_market_ids(engine)
        if not safe_ids:
            return []
        id_list = [str(i) for i in safe_ids]
        placeholders = ", ".join(f":m{i}" for i in range(len(id_list)))
        params = {f"m{i}": v for i, v in enumerate(id_list)}
        with engine.connect() as conn:
            rows = conn.execute(sa.text(
                f"SELECT Name, MarketCode FROM Markets "
                f"WHERE Id IN ({placeholders}) ORDER BY MarketCode"
            ), params).fetchall()
    except Exception:
        return []
    slugs: list[str] = []
    for name, _code in rows:
        s = _market_slug(name)
        if s and s not in slugs:
            slugs.append(s)
    return slugs


def load_crops() -> pd.DataFrame:
    """Crop identity plus agronomy (growth period, harvest window, planting season).

    Agronomy comes from CropAgronomyProfiles, not the legacy Crops columns, which were
    dropped - do not read them again. Crops still owns identity, and the profile is
    joined 1:1 with a LEFT JOIN so a crop with no profile still appears with NULL
    agronomy and is then excluded from forecasting by the label gate.

    PlantingSeason is a reconstructed legacy-compatible string, kept only so the
    downstream encoder reproduces today's per-crop values exactly: the legacy encoder
    mapped 'Year-round' to 0 but NULL to -1, and the profile month columns cannot tell
    those two apart. All months NULL plus a known growth period means 'Year-round';
    all months NULL and no growth period means None. Once real months are populated,
    Yala/Maha win regardless.

    IsVerified is returned raw for audit; the strict exclusion predicate itself lives
    in resolve_forecast_gp().
    """
    sql = """
        SELECT c.Id AS CropId, c.CropCode, c.Name AS CropName,
               p.GrowthPeriodDays, p.HarvestWindowDays,
               p.YalaPlantingStartMonth, p.YalaPlantingEndMonth,
               p.MahaPlantingStartMonth, p.MahaPlantingEndMonth,
               p.IsPerennial, p.IsVerified
        FROM Crops c
        LEFT JOIN CropAgronomyProfiles p ON p.CropId = c.Id
    """
    df = pd.read_sql(sql, get_engine())
    df["CropId"] = df["CropId"].astype(str)
    # Rebuild the legacy PlantingSeason string so the encoder stays bit-identical.
    # Month columns take precedence; today they are all NULL, so this collapses to the
    # growth-period-based Year-round/None split.
    yala_cols = ["YalaPlantingStartMonth", "YalaPlantingEndMonth"]
    maha_cols = ["MahaPlantingStartMonth", "MahaPlantingEndMonth"]
    has_yala = df[yala_cols].notna().any(axis=1)
    has_maha = df[maha_cols].notna().any(axis=1)
    season = pd.Series([None] * len(df), index=df.index, dtype=object)
    # All months NULL plus a known growth period => legacy 'Year-round' (enc 0);
    # all months NULL and no growth period => None (legacy enc -1).
    season = season.mask(df["GrowthPeriodDays"].notna() & ~has_yala & ~has_maha,
                         "Year-round")
    season = season.mask(has_maha, "Maha")   # Maha months populated (Step 5+)
    season = season.mask(has_yala, "Yala")   # Yala wins if both somehow set
    df["PlantingSeason"] = season
    return df


_CROP_CATEGORY_COLS = ["crop_id", "category_id", "category_code",
                       "category_name", "parent_code"]


def load_crop_categories() -> pd.DataFrame:
    """Crop taxonomy: one row per crop with its category and parent category.

    The DB CropCategories table is the source of truth - never hardcode a category map
    in Python. Joins Crops -> CropCategories -> parent category (LEFT, so a top-level
    category has parent_code None); a crop with no category is dropped by the inner
    join. A missing table degrades to an empty frame, like load_policy_flags().

    GUID columns are lowercased because a mixed-case miss at the .NET boundary was a
    real bug. Returns [crop_id, category_id, category_code, category_name, parent_code].
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
    """USD to LKR exchange-rate series for a point-in-time (as-of) join.

    open.er-api.com is latest-only, so historical FX is not backfilled and the series
    is sparse. Returns [date, fx_usd_lkr] sorted ascending for pd.merge_asof.
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


# Empty-frame schemas so a missing source degrades to all-NaN features instead of
# failing the build.
_POLICY_COLS = ["Id", "PolicyType", "Title", "EffectiveFrom", "EffectiveTo",
                "Direction", "Source", "ReferenceUrl"]
_SENTIMENT_COLS = ["Date", "MeanSentiment", "ArticleCount",
                   "DroughtRatio", "FloodRatio", "PolicyRatio"]
_FESTIVAL_COLS = ["FestivalKey", "Date", "LeadUpDays", "IsProvisional", "Source"]
_MACRO_COLS = ["SeriesCode", "ReferenceDate", "PublishedAt", "Value",
               "IsPublishedAtImputed"]


def load_policy_flags() -> pd.DataFrame:
    """Government-policy flags as date ranges, for an active-as-of join.

    A flag is active on date D when EffectiveFrom <= D and (EffectiveTo IS NULL or
    D <= EffectiveTo). The knowledge date is EffectiveFrom, so attaching it at D is
    leakage-safe. CreatedAtUtc is an audit field and is deliberately not loaded.
    EffectiveTo NaT means still active. Sorted by EffectiveFrom.
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
    """National festival calendar for the feature build.

    FestivalCalendarEntries is the single source of truth for festival dates; there is
    deliberately no static Python twin. A missing or empty table degrades to an empty
    frame, which attaches all-zero festival features rather than failing the build.

    The Poya calendar stays Python-static (data/poya_days.py) on purpose: its only
    consumer is data-quality gap suppression, which has to run with no DB. Festivals
    are model features, so they live in the DB where the .NET seed path owns them.

    Date is date-only, so it can never carry a hidden 'now'. CreatedAtUtc is not
    loaded. LeadUpDays comes straight from the row, which preserves the Avurudu
    Apr-13-anchored / Apr-14=0 window convention. Returns [FestivalKey, Date,
    LeadUpDays, IsProvisional, Source] sorted by Date.
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
    """Reshape load_festivals() output into a Prophet holidays frame.

    Prophet spans [ds + lower_window, ds + upper_window] and our lead-up window is
    [Date - LeadUpDays, Date], so lower_window = -LeadUpDays and upper_window = 0.
    Nothing wires Prophet up yet; this only proves the calendar is Prophet-ready.
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

    Every row carries two dates. PublishedAt is the vintage date - the first date the
    world could know the value - and is the ONLY leakage-safe join key. ReferenceDate
    is the period the value describes and is audit-only: a monthly index published
    weeks after its reference month is a classic lookahead if joined on it.

    IsPublishedAtImputed marks vintages whose PublishedAt came from a per-series
    publication-lag prior. Both date columns are pinned to [ns]. A missing table
    degrades to an empty frame (all-NaN macro features).

    Returns [SeriesCode, ReferenceDate, PublishedAt, Value, IsPublishedAtImputed]
    sorted by (SeriesCode, PublishedAt) for a per-series backward merge_asof.
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
    # Both date columns pinned to [ns]: PublishedAt is the join key, ReferenceDate only
    # travels alongside for the two-date tripwire tests.
    df["ReferenceDate"] = _as_canon_dt(df["ReferenceDate"])
    df["PublishedAt"] = _as_canon_dt(df["PublishedAt"])
    df["Value"] = df["Value"].astype(float)
    df["IsPublishedAtImputed"] = df["IsPublishedAtImputed"].astype(bool)
    return df.sort_values(["SeriesCode", "PublishedAt"]).reset_index(drop=True)


def load_news_sentiment() -> pd.DataFrame:
    """National daily news-sentiment signal for a point-in-time (as-of) join.

    NewsSentimentDaily is written by score_news.py and can be missing or empty, in
    which case the sentiment features attach as all-NaN. A day with no article is
    simply absent, so the backward join carries the last reading forward and never
    reads a date after the observation date.

    Returns [Date, MeanSentiment, ArticleCount, DroughtRatio, FloodRatio, PolicyRatio]
    sorted by Date.
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
