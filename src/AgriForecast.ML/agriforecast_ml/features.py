"""Leakage-safe feature engineering: builds CropFeatureDaily.

CARDINAL RULE: every feature for an observation on date D uses ONLY data
known on or before D. The label (harvest price at D + GrowthPeriodDays) is the
only field allowed to look into the future, and exists for training only.
"""
from __future__ import annotations

import numpy as np
import pandas as pd

# Sri Lanka cultivation seasons (rough): Maha = Oct-Mar (NE monsoon), Yala = Apr-Sep.
_MAHA_MONTHS = {10, 11, 12, 1, 2, 3}
_PLANTING_SEASON_ENC = {"Year-round": 0, "Yala": 1, "Maha": 2}
_FFILL_LIMIT = 5  # carry a known price across at most 5 non-trading days


def _is_festival(idx: pd.DatetimeIndex) -> np.ndarray:
    """Fixed-date major SL festivals that spike food demand. Lunar festivals
    (Vesak, Ramadan/Eid) vary yearly and are a TODO once a date table exists."""
    m, d = idx.month, idx.day
    avurudu = (m == 4) & (d >= 12) & (d <= 15)        # Sinhala & Tamil New Year
    pongal = (m == 1) & (d >= 14) & (d <= 15)          # Thai Pongal
    christmas = (m == 12) & (d >= 24) & (d <= 26)
    return (avurudu | pongal | christmas).astype("int8")


def _weather_lookups(weather: pd.DataFrame):
    """period(month) -> (temp, rain), plus rainfall climatology by calendar month."""
    by_month = {row.Month: (row.AvgTemperature, row.TotalRainfall) for row in weather.itertuples()}
    clim = weather.assign(mnum=weather["Month"].apply(lambda p: p.month)) \
                  .groupby("mnum")["TotalRainfall"].mean().to_dict()
    return by_month, clim


def build_crop_features(crop_id, group: pd.DataFrame, meta: pd.Series,
                        weather_by_month: dict, rain_clim: dict) -> pd.DataFrame:
    # --- 1. Continuous daily grid (calendar-consistent windows) ---
    g = group.sort_values("PriceDate").set_index("PriceDate")
    full = pd.date_range(g.index.min(), g.index.max(), freq="D")
    is_trading = g.index  # original trading days
    daily = g.reindex(full)
    daily["IsTrading"] = daily["AvgPrice"].notna()
    for col in ("AvgPrice", "MinPrice", "MaxPrice"):
        daily[col] = daily[col].ffill(limit=_FFILL_LIMIT)

    price = daily["AvgPrice"]

    # --- 2. Price features (all backward-looking; window ending at D inclusive) ---
    out = pd.DataFrame(index=daily.index)
    out["AvgPrice"] = price
    out["MinPrice"] = daily["MinPrice"]
    out["MaxPrice"] = daily["MaxPrice"]
    out["RollMean7"] = price.rolling(7, min_periods=3).mean()
    out["RollMean30"] = price.rolling(30, min_periods=10).mean()
    out["RollMean90"] = price.rolling(90, min_periods=30).mean()
    out["RollStd30"] = price.rolling(30, min_periods=10).std()
    out["RollStd90"] = price.rolling(90, min_periods=30).std()
    out["Momentum"] = out["RollMean7"] - out["RollMean30"]
    out["PriceZScore90"] = (price - out["RollMean90"]) / out["RollStd90"]
    out["Lag7"] = price.shift(7)
    out["Lag30"] = price.shift(30)
    out["Lag60"] = price.shift(60)
    out["Lag90"] = price.shift(90)
    out["PctChange30"] = price.pct_change(30, fill_method=None)

    # --- 3. Calendar / seasonality ---
    idx = out.index
    out["Year"] = idx.year
    out["MonthNum"] = idx.month
    out["WeekOfYear"] = idx.isocalendar().week.astype(int).values
    doy = idx.dayofyear
    out["DayOfYear"] = doy
    out["SinDoy"] = np.sin(2 * np.pi * doy / 365.25)
    out["CosDoy"] = np.cos(2 * np.pi * doy / 365.25)
    out["SeasonMaha"] = idx.month.isin(_MAHA_MONTHS).astype("int8")
    out["IsFestival"] = _is_festival(idx)

    # --- 4. Weather (point-in-time: use the LAST COMPLETE month, i.e. M-1) ---
    obs_period = idx.to_period("M")
    def wx(p, which):
        v = weather_by_month.get(p)
        return np.nan if v is None else v[which]
    cur = obs_period - 1                       # last complete month at observation time
    out["WxAvgTempC"] = [wx(p, 0) for p in cur]
    out["WxRainfallMm"] = [wx(p, 1) for p in cur]
    out["WxTempLag1"] = [wx(p, 0) for p in (obs_period - 2)]
    out["WxTempLag2"] = [wx(p, 0) for p in (obs_period - 3)]
    out["WxRainLag1"] = [wx(p, 1) for p in (obs_period - 2)]
    out["WxRainLag2"] = [wx(p, 1) for p in (obs_period - 3)]
    out["WxRainAnomaly"] = [
        (wx(p, 1) - rain_clim.get(p.month, np.nan)) if weather_by_month.get(p) else np.nan
        for p in cur
    ]

    # --- 5. Crop metadata ---
    gp = meta["GrowthPeriodDays"]
    gp = int(gp) if pd.notna(gp) else None
    out["GrowthPeriodDays"] = gp if gp is not None else np.nan
    out["PlantingSeasonEnc"] = _PLANTING_SEASON_ENC.get(meta["PlantingSeason"], -1)
    out["HarvestWindowDays"] = meta["HarvestWindowDays"] if pd.notna(meta["HarvestWindowDays"]) else np.nan

    # --- 6. Label: harvest price = price gp calendar days AHEAD (future; train only) ---
    if gp is not None:
        out["HarvestDate"] = out.index + pd.Timedelta(days=gp)
        out["LabelHarvestPrice"] = price.shift(-gp)
        out["LabelAvailable"] = out["LabelHarvestPrice"].notna().astype("int8")
    else:
        out["HarvestDate"] = pd.NaT
        out["LabelHarvestPrice"] = np.nan
        out["LabelAvailable"] = np.int8(0)

    # --- 7. Keep only real trading days; attach keys ---
    out = out.loc[is_trading].copy()
    out.insert(0, "ObservationDate", out.index)
    out.insert(0, "CropName", meta["CropName"])
    out.insert(0, "CropCode", meta["CropCode"])
    out.insert(0, "CropId", str(crop_id))
    return out.reset_index(drop=True)


def _attach_fx(result: pd.DataFrame, fx: pd.DataFrame | None) -> pd.DataFrame:
    """National FxUsdLkr column via point-in-time (as-of, backward) merge.

    For each ObservationDate D, take the most recent FX with date <= D (NaN if
    none). Same value across all crops for a given date — this is a national
    indicator, not crop-specific. CARDINAL RULE: never an FX date AFTER D.
    """
    if fx is None or fx.empty:
        result["FxUsdLkr"] = np.nan
        return result
    fx_sorted = fx[["date", "fx_usd_lkr"]].dropna(subset=["date"]).sort_values("date")
    merged = pd.merge_asof(
        result.sort_values("ObservationDate"),
        fx_sorted,
        left_on="ObservationDate",
        right_on="date",
        direction="backward",
    )
    merged = merged.rename(columns={"fx_usd_lkr": "FxUsdLkr"}).drop(columns=["date"])
    return merged


def build_all(prices: pd.DataFrame, crops: pd.DataFrame, weather: pd.DataFrame,
              fx: pd.DataFrame | None = None) -> pd.DataFrame:
    weather_by_month, rain_clim = _weather_lookups(weather)
    meta_by_crop = crops.set_index("CropId")
    frames = []
    for crop_id, group in prices.groupby("CropId"):
        if crop_id not in meta_by_crop.index:
            continue
        meta = meta_by_crop.loc[crop_id]
        frames.append(build_crop_features(crop_id, group, meta, weather_by_month, rain_clim))
    result = pd.concat(frames, ignore_index=True)
    result["HarvestDate"] = pd.to_datetime(result["HarvestDate"])
    result = _attach_fx(result, fx)
    return result
