"""Leakage-safe feature engineering: builds the CropFeatureDaily table.

Every feature for an observation on date D uses only data known on or before D.
The harvest-price label is the only field that looks ahead; it exists for training
only.
"""
from __future__ import annotations

import warnings

import numpy as np
import pandas as pd

from .load import resolve_forecast_gp

# merge_asof needs both join keys in the same datetime64 unit (pandas 3.0).
_MERGE_DT = "datetime64[ns]"


def _canon_key(s: pd.Series) -> pd.Series:
    return pd.to_datetime(s).astype(_MERGE_DT)


# Sri Lanka cultivation seasons: Maha = Oct-Mar, Yala = Apr-Sep.
_MAHA_MONTHS = {10, 11, 12, 1, 2, 3}
_PLANTING_SEASON_ENC = {"Year-round": 0, "Yala": 1, "Maha": 2}
_FFILL_LIMIT = 5  # carry a known price across at most 5 non-trading days

# Clip the countdown: past about a month the exact distance stops mattering.
# Fixed domain prior - tuning the window on price data would itself be leakage.
_FESTIVAL_CLIP_DAYS = 30

# Lead-up window lengths are read per row from FestivalCalendarEntries.LeadUpDays,
# not hardcoded here (the Apr 14 Avurudu row has 0 so the window is not counted twice).

# Festivals big enough to get their own lead-up boolean column.
_LEADUP_FESTIVALS = {"AVURUDU": "InLeadupAvurudu", "CHRISTMAS": "InLeadupChristmas"}

# Every festival column this module emits.
FESTIVAL_FEATURE_COLS = [
    "HarvestInFestivalLeadup",
    "DaysFromHarvestToNextFestival",
    "DaysToNextFestivalAny",
    "InLeadupAvurudu",
    "InLeadupChristmas",
]


def _festival_windows(festivals: pd.DataFrame | None):
    """Turn the festival calendar into lookup arrays.

    Returns the sorted event dates and the lead-up windows [Date - LeadUpDays, Date].
    Window key None is the merged 'any festival' window, other keys are per-festival.
    Rows with LeadUpDays <= 0 add no window.
    """
    events: list = []
    windows: dict = {None: []}
    if festivals is not None and not festivals.empty:
        for r in festivals.itertuples():
            d = pd.Timestamp(r.Date).normalize()
            events.append(np.datetime64(d.date(), "D"))
            lead = int(r.LeadUpDays)
            if lead > 0:
                start = np.datetime64((d - pd.Timedelta(days=lead)).date(), "D")
                end = np.datetime64(d.date(), "D")
                windows[None].append((start, end))
                key = str(r.FestivalKey)
                if key in _LEADUP_FESTIVALS:
                    windows.setdefault(key, []).append((start, end))
    events_arr = np.array(sorted(set(events)), dtype="datetime64[D]")
    return events_arr, windows


def _in_any_window(dates_d: np.ndarray, intervals: list) -> np.ndarray:
    """Bool mask: is each date inside any closed [start, end] interval."""
    mask = np.zeros(dates_d.shape[0], dtype=bool)
    for start, end in intervals:
        mask |= (dates_d >= start) & (dates_d <= end)
    return mask


def _days_to_next_event(dates_d: np.ndarray, events_arr: np.ndarray,
                        clip: int) -> np.ndarray:
    """Whole days from each date to the next festival on or after it, clipped to [0, clip]."""
    n = dates_d.shape[0]
    if events_arr.size == 0:
        return np.full(n, float(clip), dtype="float64")
    idx = np.searchsorted(events_arr, dates_d, side="left")
    out = np.full(n, float(clip), dtype="float64")
    valid = idx < events_arr.size
    diff = (events_arr[np.clip(idx, 0, events_arr.size - 1)] - dates_d).astype(
        "timedelta64[D]").astype("float64")
    out[valid] = diff[valid]
    np.clip(out, 0.0, float(clip), out=out)
    return out


def _festival_features(observation_dates: pd.Series, harvest_dates: pd.Series,
                       events_arr: np.ndarray, windows: dict) -> pd.DataFrame:
    """Build the festival columns for aligned observation and harvest dates.

    Harvest-anchored (the label is a harvest-time price): HarvestInFestivalLeadup,
    DaysFromHarvestToNextFestival. Observation-anchored: DaysToNextFestivalAny,
    InLeadupAvurudu, InLeadupChristmas. A NaT harvest date gets 0 / clip so the
    columns stay numeric.
    """
    obs_d = pd.to_datetime(observation_dates).values.astype("datetime64[D]")
    harv = pd.to_datetime(harvest_dates)
    harv_valid = harv.notna().values
    harv_d = harv.fillna(observation_dates.iloc[0] if len(observation_dates) else pd.Timestamp("2000-01-01"))
    harv_d = pd.to_datetime(harv_d).values.astype("datetime64[D]")

    out = pd.DataFrame(index=observation_dates.index)

    # Harvest-anchored
    h_in_leadup = _in_any_window(harv_d, windows.get(None, []))
    h_in_leadup = h_in_leadup & harv_valid  # NaT harvest -> not in any window
    out["HarvestInFestivalLeadup"] = h_in_leadup.astype("int8")
    h_days = _days_to_next_event(harv_d, events_arr, _FESTIVAL_CLIP_DAYS)
    h_days[~harv_valid] = float(_FESTIVAL_CLIP_DAYS)  # unknown harvest -> "far"
    out["DaysFromHarvestToNextFestival"] = h_days

    # Observation-anchored
    out["DaysToNextFestivalAny"] = _days_to_next_event(obs_d, events_arr,
                                                       _FESTIVAL_CLIP_DAYS)
    out["InLeadupAvurudu"] = _in_any_window(
        obs_d, windows.get("AVURUDU", [])).astype("int8")
    out["InLeadupChristmas"] = _in_any_window(
        obs_d, windows.get("CHRISTMAS", [])).astype("int8")
    return out[FESTIVAL_FEATURE_COLS]


# Calendar columns that depend only on a date. Shared with the serving what-if sweep
# so a candidate planting date is encoded exactly like a training date.
CALENDAR_FEATURE_COLS = [
    "Year", "MonthNum", "WeekOfYear", "DayOfYear", "SinDoy", "CosDoy", "SeasonMaha",
]


def calendar_features(dates) -> pd.DataFrame:
    """Calendar and seasonality columns for a sequence of dates.

    Depends on the dates alone, so it is valid both for historical observation dates
    and for future candidate planting dates in the serving sweep.
    """
    idx = pd.DatetimeIndex(dates)
    doy = idx.dayofyear
    return pd.DataFrame({
        "Year": idx.year,
        "MonthNum": idx.month,
        "WeekOfYear": idx.isocalendar().week.astype(int).values,
        "DayOfYear": doy,
        "SinDoy": np.sin(2 * np.pi * doy / 365.25),
        "CosDoy": np.cos(2 * np.pi * doy / 365.25),
        "SeasonMaha": idx.month.isin(_MAHA_MONTHS).astype("int8"),
    }, index=idx)[CALENDAR_FEATURE_COLS]


def _weather_lookups(weather: pd.DataFrame):
    """period(month) -> (temp, rain), plus rainfall climatology by calendar month."""
    by_month = {row.Month: (row.AvgTemperature, row.TotalRainfall) for row in weather.itertuples()}
    clim = weather.assign(mnum=weather["Month"].apply(lambda p: p.month)) \
                  .groupby("mnum")["TotalRainfall"].mean().to_dict()
    return by_month, clim


def build_crop_features(crop_id, group: pd.DataFrame, meta: pd.Series,
                        weather_by_month: dict, rain_clim: dict) -> pd.DataFrame:
    # Continuous daily grid, so rolling windows are calendar-consistent.
    g = group.sort_values("PriceDate").set_index("PriceDate")
    if g.index.has_duplicates:
        # Defence in depth: load_prices() already averages duplicate (crop, source, date) rows.
        # Collapsing again here stops reindex() below hitting duplicate labels.
        g = g[["MinPrice", "MaxPrice", "AvgPrice"]].groupby(level=0).mean()
    full = pd.date_range(g.index.min(), g.index.max(), freq="D")
    is_trading = g.index  # original trading days
    daily = g.reindex(full)
    daily["IsTrading"] = daily["AvgPrice"].notna()
    for col in ("AvgPrice", "MinPrice", "MaxPrice"):
        daily[col] = daily[col].ffill(limit=_FFILL_LIMIT)

    price = daily["AvgPrice"]

    # Price features: all backward-looking, window ends at D inclusive.
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

    # Shared with serving through calendar_features() - do not re-inline these formulas.
    idx = out.index
    cal = calendar_features(idx)
    for col in CALENDAR_FEATURE_COLS:
        out[col] = cal[col]
    # Festival features are attached in build_all() from the DB calendar.

    # Weather is point-in-time: use the last COMPLETE month (M-1), never the current one.
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

    # A crop only gets a harvest horizon if its agronomy profile is verified and has a
    # GrowthPeriodDays; otherwise there is no label and the crop drops out of training.
    gp = resolve_forecast_gp(meta.get("IsVerified"), meta["GrowthPeriodDays"])
    out["GrowthPeriodDays"] = gp if gp is not None else np.nan
    out["PlantingSeasonEnc"] = _PLANTING_SEASON_ENC.get(meta["PlantingSeason"], -1)
    out["HarvestWindowDays"] = meta["HarvestWindowDays"] if pd.notna(meta["HarvestWindowDays"]) else np.nan

    # Label: the price gp calendar days ahead. The only forward-looking field, training only.
    if gp is not None:
        out["HarvestDate"] = out.index + pd.Timedelta(days=gp)
        out["LabelHarvestPrice"] = price.shift(-gp)
        out["LabelAvailable"] = out["LabelHarvestPrice"].notna().astype("int8")
    else:
        out["HarvestDate"] = pd.NaT
        out["LabelHarvestPrice"] = np.nan
        out["LabelAvailable"] = np.int8(0)

    # Keep only real trading days; attach keys.
    out = out.loc[is_trading].copy()
    out.insert(0, "ObservationDate", out.index)
    out.insert(0, "CropName", meta["CropName"])
    out.insert(0, "CropCode", meta["CropCode"])
    out.insert(0, "CropId", str(crop_id))
    return out.reset_index(drop=True)


def _attach_fx(result: pd.DataFrame, fx: pd.DataFrame | None) -> pd.DataFrame:
    """National FxUsdLkr via a backward as-of merge.

    Takes the latest FX with date <= D, or NaN. Never an FX date after D. Same value
    for every crop on a given date.
    """
    if fx is None or fx.empty:
        result["FxUsdLkr"] = np.nan
        return result
    fx_sorted = fx[["date", "fx_usd_lkr"]].dropna(subset=["date"]).sort_values("date")
    fx_sorted["date"] = _canon_key(fx_sorted["date"])
    result = result.copy()
    result["ObservationDate"] = _canon_key(result["ObservationDate"])
    merged = pd.merge_asof(
        result.sort_values("ObservationDate"),
        fx_sorted,
        left_on="ObservationDate",
        right_on="date",
        direction="backward",
    )
    merged = merged.rename(columns={"fx_usd_lkr": "FxUsdLkr"}).drop(columns=["date"])
    return merged


# Sentiment columns attached at observation time: overall mood plus topic ratios.
_SENTIMENT_FEATURES = ["MeanSentiment", "DroughtRatio", "FloodRatio", "PolicyRatio"]

# Mirrors the PolicyType enum in AgriForecast.Domain.Enums.
_POLICY_IMPORT_BAN = 1
_POLICY_PRICE_CEILING = 3
_POLICY_FERTILISER_SUBSIDY = 5
_POLICY_FEATURES = ["ActivePolicyNetDirection", "ActivePolicyCount",
                    "PolicyImportBanActive", "PolicyPriceCeilingActive",
                    "PolicyFertiliserSubsidyActive"]


def _attach_sentiment(result: pd.DataFrame, sentiment: pd.DataFrame | None) -> pd.DataFrame:
    """National news-sentiment columns via a backward as-of merge.

    Carries the last reading with Date <= D forward and never reads a date after D.
    Missing sentiment leaves the columns NaN.
    """
    if sentiment is None or sentiment.empty:
        for col in _SENTIMENT_FEATURES:
            result[col] = np.nan
        return result
    cols = ["Date"] + _SENTIMENT_FEATURES
    s_sorted = sentiment[cols].dropna(subset=["Date"]).sort_values("Date")
    s_sorted["Date"] = _canon_key(s_sorted["Date"])
    result = result.copy()
    result["ObservationDate"] = _canon_key(result["ObservationDate"])
    merged = pd.merge_asof(
        result.sort_values("ObservationDate"),
        s_sorted,
        left_on="ObservationDate",
        right_on="Date",
        direction="backward",
    )
    return merged.drop(columns=["Date"])


def _attach_policy(result: pd.DataFrame, policy: pd.DataFrame | None) -> pd.DataFrame:
    """Which government policies are active at observation time.

    A flag is active on D when EffectiveFrom <= D and (EffectiveTo is NULL or
    D <= EffectiveTo), so the knowledge date is EffectiveFrom and this is leakage-safe.
    Adds ActivePolicyNetDirection, ActivePolicyCount and the ImportBan / PriceCeiling /
    FertiliserSubsidy booleans. No active policy means 0, not NaN.
    """
    out = result.copy()
    n = len(out)
    if policy is None or policy.empty:
        out["ActivePolicyNetDirection"] = np.zeros(n, dtype="float64")
        out["ActivePolicyCount"] = np.zeros(n, dtype="int64")
        out["PolicyImportBanActive"] = np.zeros(n, dtype="int8")
        out["PolicyPriceCeilingActive"] = np.zeros(n, dtype="int8")
        out["PolicyFertiliserSubsidyActive"] = np.zeros(n, dtype="int8")
        return out

    D = out["ObservationDate"].to_numpy(dtype="datetime64[ns]")
    eff_from = policy["EffectiveFrom"].to_numpy(dtype="datetime64[ns]")
    eff_to = policy["EffectiveTo"].to_numpy(dtype="datetime64[ns]")  # NaT = open
    direction = policy["Direction"].to_numpy(dtype="float64")
    ptype = policy["PolicyType"].to_numpy()

    # active[i, j] = flag j is active on observation date i.
    started = D[:, None] >= eff_from[None, :]
    # A NaT EffectiveTo means open-ended; NaT comparisons are False, so OR it in.
    open_ended = np.isnat(eff_to)
    not_ended = (D[:, None] <= eff_to[None, :]) | open_ended[None, :]
    active = started & not_ended  # (n_dates, n_flags) bool

    # Mask-and-sum rather than matmul, which raised spurious warnings on this BLAS build.
    out["ActivePolicyNetDirection"] = \
        np.where(active, direction[None, :], 0.0).sum(axis=1)
    out["ActivePolicyCount"] = active.sum(axis=1).astype("int64")
    out["PolicyImportBanActive"] = \
        (active & (ptype == _POLICY_IMPORT_BAN)[None, :]).any(axis=1).astype("int8")
    out["PolicyPriceCeilingActive"] = \
        (active & (ptype == _POLICY_PRICE_CEILING)[None, :]).any(axis=1).astype("int8")
    out["PolicyFertiliserSubsidyActive"] = \
        (active & (ptype == _POLICY_FERTILISER_SUBSIDY)[None, :]).any(axis=1).astype("int8")
    return out


# CBSL SeriesCode -> feature column. National signal, identical across crops.
# YoY series only: raw index levels change meaning when CBSL rebases.
_MACRO_SERIES = {
    "CCPI_FOOD_YOY_BASE2021": "MacroFoodInflationYoY",
    "FOOD_IMPORTS_YOY": "MacroFoodImportsYoY",
    "POLICY_RATE_OPR": "MacroPolicyRateOPR",
}
_MACRO_FEATURES = list(_MACRO_SERIES.values())

# A monthly vintage older than this at the observation date becomes NaN instead of
# being carried forward. FX and sentiment are near-daily and carry forward uncapped.
_MACRO_STALENESS_DAYS = 60


def _attach_macro(result: pd.DataFrame, macro: pd.DataFrame | None) -> pd.DataFrame:
    """CBSL macro columns via a backward as-of merge on the vintage date, per SeriesCode.

    Joins on PublishedAt, never ReferenceDate: PublishedAt is when the value could be
    known, ReferenceDate is only the period it describes. A vintage older than
    _MACRO_STALENESS_DAYS at the observation date becomes NaN, and anything not
    knowable is NaN rather than 0 (unlike _attach_policy).
    """
    out = result.copy()
    out["ObservationDate"] = _canon_key(out["ObservationDate"])
    if macro is None or macro.empty:
        for col in _MACRO_FEATURES:
            out[col] = np.nan
        return out

    # merge_asof needs a sorted left key, so the frame comes back sorted by ObservationDate.
    out = out.sort_values("ObservationDate").reset_index(drop=True)

    for series_code, feat_col in _MACRO_SERIES.items():
        sub = macro[macro["SeriesCode"] == series_code]
        # ReferenceDate is never a join key; only PublishedAt, the date the value was knowable.
        sub = (sub[["PublishedAt", "Value"]]
               .dropna(subset=["PublishedAt"])
               .sort_values("PublishedAt"))
        if sub.empty:
            # Series absent from this DB -> not knowable -> NaN, never 0.
            out[feat_col] = np.nan
            continue
        right = sub.rename(columns={"PublishedAt": "_pub", "Value": feat_col})
        right["_pub"] = _canon_key(right["_pub"])
        merged = pd.merge_asof(
            out[["ObservationDate"]],
            right,
            left_on="ObservationDate",
            right_on="_pub",
            direction="backward",
        )
        vals = merged[feat_col].to_numpy(dtype="float64")
        # Staleness cap: age of the matched vintage at the observation date.
        pub = merged["_pub"].to_numpy(dtype="datetime64[ns]")
        obs = out["ObservationDate"].to_numpy(dtype="datetime64[ns]")
        age_days = (obs - pub) / np.timedelta64(1, "D")
        # No match or too stale -> NaN. Strict '>': exactly at the cap is still usable.
        too_stale = np.isnat(pub) | (age_days > _MACRO_STALENESS_DAYS)
        vals = np.where(too_stale, np.nan, vals)
        out[feat_col] = vals

    return out


# Cross-market spread features: per-market point-in-time prices attached to the
# per-(crop, date) frame, computed offline so serving still reads one row per crop
# and date. A market that did not report gives NaN, never 0. National = unweighted
# mean over the feature-safe markets that reported, and NaN when fewer than 2 do.
# Dambulla is the reference market for the spread and rank columns.
_SPREAD_REFERENCE_SLUG = "Dambulla"

# A per-market price carried forward past this many days becomes NaN: 99.8% of real
# reporting gaps are <= 14 days, so a longer gap means the market stopped reporting.
_SPREAD_STALENESS_DAYS = 14

# Spread columns that are not per-market (the Mkt<Slug>* ones are generated).
_SPREAD_DERIVED_COLS = [
    "SpreadVsNational",
    "MarketRankPct",
    "LeaderMarketLag7",
    "NMarketsReporting",
]


def _asof_market_price(result: pd.DataFrame, sub: pd.DataFrame,
                       left_date_col: str, out_col: str) -> pd.Series:
    """Backward as-of merge of one market's daily AvgPrice onto result, per crop.

    Takes the latest AvgPrice with ObservedDate <= the left date, or NaN when that
    observation is more than _SPREAD_STALENESS_DAYS old.
    """
    n = len(result)
    if sub.empty:
        return pd.Series(np.full(n, np.nan), index=result.index)
    # merge_asof resets the index, so carry the original one along to realign afterwards.
    left = result[["CropId", left_date_col]].copy()
    left["_orig_idx"] = result.index
    left[left_date_col] = _canon_key(left[left_date_col])
    right = sub[["CropId", "ObservedDate", "AvgPrice"]].copy()
    right["ObservedDate"] = _canon_key(right["ObservedDate"])
    left = left.sort_values(left_date_col)
    right = right.sort_values("ObservedDate")
    merged = pd.merge_asof(
        left, right,
        left_on=left_date_col, right_on="ObservedDate",
        by="CropId", direction="backward",
    )
    vals = merged["AvgPrice"].to_numpy(dtype="float64")
    obs = merged["ObservedDate"].to_numpy(dtype="datetime64[ns]")
    ld = merged[left_date_col].to_numpy(dtype="datetime64[ns]")
    age = (ld - obs) / np.timedelta64(1, "D")
    too_stale = np.isnat(obs) | (age > _SPREAD_STALENESS_DAYS)
    vals = np.where(too_stale, np.nan, vals)
    return pd.Series(vals, index=merged["_orig_idx"].to_numpy()).reindex(result.index)


def _attach_market_spread(result: pd.DataFrame,
                          price_obs: pd.DataFrame | None,
                          market_slugs: list | None) -> pd.DataFrame:
    """Cross-market spread features, one backward as-of merge per market.

    Every derived column (spread / rank / leader / count) is built from the already
    as-of'd per-market columns, so it inherits the same point-in-time gate. A market
    with no price gives NaN, never 0.
    """
    out = result.copy()
    out["ObservationDate"] = _canon_key(out["ObservationDate"])
    slugs = list(market_slugs) if market_slugs else []

    # Per-market AvgPrice + 7-day-lagged AvgPrice columns (one pair per market).
    avg_cols: list[str] = []
    lag_cols: list[str] = []
    if price_obs is None:
        price_obs = pd.DataFrame(columns=["MarketSlug", "CropId", "ObservedDate", "AvgPrice"])

    # The Lag7 leg as-ofs onto D-7, which keeps the same point-in-time gate.
    lag_date = (out["ObservationDate"] - pd.Timedelta(days=7))
    out["_lag7_date"] = lag_date

    for slug in slugs:
        avg_col = f"Mkt{slug}AvgPrice"
        lag_col = f"Mkt{slug}Lag7"
        avg_cols.append(avg_col)
        lag_cols.append(lag_col)
        sub = price_obs[price_obs["MarketSlug"] == slug] if len(price_obs) else price_obs
        out[avg_col] = _asof_market_price(out, sub, "ObservationDate", avg_col)
        out[lag_col] = _asof_market_price(out, sub, "_lag7_date", lag_col)

    out = out.drop(columns=["_lag7_date"])

    # Derived summaries come from the per-market columns only, never a fresh query.
    if avg_cols:
        avg_mat = out[avg_cols].to_numpy(dtype="float64")  # (n_rows, n_markets)
    else:
        avg_mat = np.empty((len(out), 0), dtype="float64")

    n_reporting = np.sum(~np.isnan(avg_mat), axis=1) if avg_mat.shape[1] else \
        np.zeros(len(out), dtype="int64")
    out["NMarketsReporting"] = n_reporting.astype("int64")

    # National = unweighted mean over reporting markets; NaN when fewer than 2 report.
    # An all-NaN row warns 'Mean of empty slice'; the NaN is intended, so silence it.
    enough = n_reporting >= 2
    if avg_mat.shape[1]:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", category=RuntimeWarning)
            national = np.nanmean(avg_mat, axis=1)
    else:
        national = np.full(len(out), np.nan)
    national = np.where(enough, national, np.nan)

    # Spread and rank are relative to Dambulla; NaN if it or fewer than 2 markets report.
    ref_col = f"Mkt{_SPREAD_REFERENCE_SLUG}AvgPrice"
    ref = out[ref_col].to_numpy(dtype="float64") if ref_col in out.columns \
        else np.full(len(out), np.nan)

    spread = np.where(enough, ref - national, np.nan)
    out["SpreadVsNational"] = spread

    # market_rank_pct: share of reporting markets priced at or below the reference market.
    if avg_mat.shape[1]:
        le = np.where(np.isnan(avg_mat), np.nan, (avg_mat <= ref[:, None]).astype("float64"))
        n_le = np.nansum(le, axis=1)
        with np.errstate(invalid="ignore"):
            rank_pct = n_le / n_reporting
        ref_reports = ~np.isnan(ref)
        rank_pct = np.where(enough & ref_reports, rank_pct, np.nan)
    else:
        rank_pct = np.full(len(out), np.nan)
    out["MarketRankPct"] = rank_pct

    # leader_market_lag7: the Lag7 price of the market with the highest current AvgPrice.
    if avg_cols:
        lag_mat = out[lag_cols].to_numpy(dtype="float64")  # aligned to avg_cols
        # argmax over current AvgPrice, ignoring NaN rows safely.
        leader = np.full(len(out), np.nan)
        has_any = np.any(~np.isnan(avg_mat), axis=1)
        idx = np.full(len(out), -1, dtype="int64")
        if avg_mat.shape[1]:
            safe = np.where(np.isnan(avg_mat), -np.inf, avg_mat)
            idx = np.argmax(safe, axis=1)
        rows = np.arange(len(out))
        picked = np.where(has_any, lag_mat[rows, idx], np.nan)
        leader = np.where(enough, picked, np.nan)
    else:
        leader = np.full(len(out), np.nan)
    out["LeaderMarketLag7"] = leader

    return out


def _attach_festivals(result: pd.DataFrame,
                      festivals: pd.DataFrame | None) -> pd.DataFrame:
    """National festival features from the DB calendar (load_festivals()).

    Identical across crops for a given (ObservationDate, HarvestDate). An empty
    calendar zero-fills the columns: there is genuinely no festival signal.
    """
    events_arr, windows = _festival_windows(festivals)
    if events_arr.size == 0:
        for col in FESTIVAL_FEATURE_COLS:
            if col == "DaysToNextFestivalAny" or col == "DaysFromHarvestToNextFestival":
                result[col] = float(_FESTIVAL_CLIP_DAYS)
            else:
                result[col] = np.int8(0)
        return result
    feats = _festival_features(result["ObservationDate"], result["HarvestDate"],
                               events_arr, windows)
    for col in FESTIVAL_FEATURE_COLS:
        result[col] = feats[col].values
    return result


def build_all(prices: pd.DataFrame, crops: pd.DataFrame, weather: pd.DataFrame,
              fx: pd.DataFrame | None = None,
              sentiment: pd.DataFrame | None = None,
              policy: pd.DataFrame | None = None,
              festivals: pd.DataFrame | None = None,
              macro: pd.DataFrame | None = None,
              price_obs: pd.DataFrame | None = None,
              market_slugs: list | None = None) -> pd.DataFrame:
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
    # National signals attach AFTER the per-crop build (each is point-in-time).
    result = _attach_fx(result, fx)
    result = _attach_sentiment(result, sentiment)
    result = _attach_policy(result, policy)
    # Macro joins on the vintage date, with a staleness cap and NaN (not 0) when unknown.
    result = _attach_macro(result, macro)
    result = _attach_festivals(result, festivals)
    # Cross-market spread context, computed offline into the store.
    result = _attach_market_spread(result, price_obs, market_slugs)
    return result
