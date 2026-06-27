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
