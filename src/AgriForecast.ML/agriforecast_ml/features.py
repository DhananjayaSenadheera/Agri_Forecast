"""Leakage-safe feature engineering: builds CropFeatureDaily.

CARDINAL RULE: every feature for an observation on date D uses ONLY data
known on or before D. The label (harvest price at D + GrowthPeriodDays) is the
only field allowed to look into the future, and exists for training only.
"""
from __future__ import annotations

import warnings

import numpy as np
import pandas as pd

# Canonical datetime resolution for merge_asof join keys. pandas 3.0 requires
# both as-of keys to share the SAME datetime64 unit (read_sql may yield [s]/[us]
# and pd.to_datetime preserves it). The load layer (load._as_canon_dt) already
# normalizes every column to [ns]; pinning the keys here too makes the as-of
# merges self-defending against any future dtype drift. Pure dtype normalization
# -- same wall-clock dates, no direction/tolerance/logic change.
_MERGE_DT = "datetime64[ns]"


def _canon_key(s: pd.Series) -> pd.Series:
    return pd.to_datetime(s).astype(_MERGE_DT)


# Sri Lanka cultivation seasons (rough): Maha = Oct-Mar (NE monsoon), Yala = Apr-Sep.
_MAHA_MONTHS = {10, 11, 12, 1, 2, 3}
_PLANTING_SEASON_ENC = {"Year-round": 0, "Yala": 1, "Maha": 2}
_FFILL_LIMIT = 5  # carry a known price across at most 5 non-trading days

# Days-to-next-festival clip cap. Raw unclipped countdown is a slow ramp over the
# whole inter-festival gap (~months), which would dominate tree splits with a
# signal that is mostly "how far into the gap are we", not "is a festival near".
# Clipping to [0, cap] makes the feature ~flat outside the demand-relevant window
# and monotone inside it (trees are invariant to monotone encodings, so a clipped
# linear countdown is the right XGBoost encoding -- no buckets/decay needed). Cap
# is a fixed DOMAIN PRIOR (~a month), deliberately NOT tuned (1-2 events per CV
# fold => tuning the window boundary is itself leakage; P4 makes it a learned HP).
_FESTIVAL_CLIP_DAYS = 30

# The per-festival lead-up WINDOW length is NOT a Python constant: it is read
# per-row from FestivalCalendarEntries.LeadUpDays (the .NET seed is the single
# source of truth). This preserves the Avurudu convention -- the Apr 13 row
# carries LeadUpDays=14 and anchors the window; the Apr 14 row carries
# LeadUpDays=0 so the demand window is not double-counted. _FESTIVAL_CLIP_DAYS
# above governs only the observation/harvest COUNTDOWN clip, a separate concern.

# Per-festival lead-up booleans we surface (merged "any" is the primary signal;
# these two are the highest-volume festivals worth an individual column).
_LEADUP_FESTIVALS = {"AVURUDU": "InLeadupAvurudu", "CHRISTMAS": "InLeadupChristmas"}

# Every festival feature column this module emits (asserted zero-filled when the
# calendar is empty, and used by tests as the canonical name list).
FESTIVAL_FEATURE_COLS = [
    "HarvestInFestivalLeadup",
    "DaysFromHarvestToNextFestival",
    "DaysToNextFestivalAny",
    "InLeadupAvurudu",
    "InLeadupChristmas",
]


def _festival_windows(festivals: pd.DataFrame | None):
    """Precompute, from load_festivals() output, the arrays a pure date->feature
    lookup needs. Returns (event_dates_sorted, leadup_intervals) where:
      event_dates_sorted : np.ndarray[datetime64[D]] of every festival Date (for
                           "days to NEXT festival" -- the event itself, not its
                           lead-up).
      leadup_intervals   : dict[str|None, list[(start, end)]] closed windows
                           [Date - LeadUpDays, Date]; key None = merged "any"
                           festival, key = FestivalKey for per-festival booleans.
                           Rows with LeadUpDays<=0 contribute no window (Apr 14).
    A pure function of the calendar only -- no price data, no wall-clock.
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
    """For each date, whole days until the next festival Date >= it, clipped to
    [0, clip]. NaN (encoded as clip) when no future event exists in the calendar
    for that date. Pure: uses only the supplied dates + calendar."""
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
    """Build all festival feature columns for aligned observation/harvest dates.

    HARVEST-ANCHORED (load-bearing -- the label is harvest-time price):
      HarvestInFestivalLeadup       : HarvestDate inside ANY festival lead-up window
      DaysFromHarvestToNextFestival : clipped countdown from HarvestDate to next event
    OBSERVATION-ANCHORED (secondary):
      DaysToNextFestivalAny         : clipped countdown from ObservationDate
      InLeadupAvurudu / InLeadupChristmas : ObservationDate inside that festival's window

    Pure calendar function: no price aggregates, no now()/today(). NaT harvest
    dates (crops with no GrowthPeriodDays) get 0 / clip so the columns stay numeric
    and never NaN-poison the pooled model.
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
    # Festival features are attached in build_all() from the DB calendar
    # (load_festivals) -- a national point-in-time signal, not derived here from
    # hardcoded dates. The old _is_festival() month/day heuristic was deleted so
    # there is only ONE festival definition (the seed table).

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


# National news-sentiment columns attached at observation time. Kept lean: the
# overall mood plus the topic ratios most relevant to a price go/no-go decision.
_SENTIMENT_FEATURES = ["MeanSentiment", "DroughtRatio", "FloodRatio", "PolicyRatio"]

# PolicyType enum (mirror of AgriForecast.Domain.Enums.PolicyType). Only the
# few types whose active state is decision-relevant for price are exposed as
# per-type booleans.
_POLICY_IMPORT_BAN = 1
_POLICY_PRICE_CEILING = 3
_POLICY_FERTILISER_SUBSIDY = 5
_POLICY_FEATURES = ["ActivePolicyNetDirection", "ActivePolicyCount",
                    "PolicyImportBanActive", "PolicyPriceCeilingActive",
                    "PolicyFertiliserSubsidyActive"]


def _attach_sentiment(result: pd.DataFrame, sentiment: pd.DataFrame | None) -> pd.DataFrame:
    """National news-sentiment columns via point-in-time (as-of, backward) merge.

    Mirrors _attach_fx exactly: for each ObservationDate D take the most recent
    sentiment row with Date <= D (NaN if none / no news yet). Same value across
    all crops for a given date -- national signal. No-article days are absent in
    NewsSentimentDaily, so the backward join simply carries the last known
    reading forward; it NEVER reads a Date AFTER D. Empty/missing sentiment ->
    all feature columns are NaN.
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
    """Active-government-policy signal at observation time.

    NOT an as-of merge: policy flags are date RANGES, not a point series. A flag
    is active on date D iff EffectiveFrom <= D AND (EffectiveTo IS NULL OR
    D <= EffectiveTo). Knowledge date = EffectiveFrom, so this is leakage-safe.
    National signal -> identical across crops for a given D.

    Attaches:
      ActivePolicyNetDirection    sum of Direction over flags active on D
      ActivePolicyCount           number of flags active on D
      PolicyImportBanActive       1 if an ImportBan(1) flag is active on D
      PolicyPriceCeilingActive    1 if a PriceCeiling(3) flag is active on D
      PolicyFertiliserSubsidyActive 1 if a FertiliserSubsidy(5) flag active on D

    Empty/missing policy -> net direction 0, count 0, booleans 0 (there is
    genuinely no active policy, so zero is the correct value, not NaN).
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

    # Vectorised per-(date, flag) overlap. ~6 flags so the outer product is tiny.
    # active[i, j] = flag j is active on observation date i.
    started = D[:, None] >= eff_from[None, :]
    # EffectiveTo is NaT -> open-ended -> always within range; NaT comparison is
    # False, so OR it in explicitly.
    open_ended = np.isnat(eff_to)
    not_ended = (D[:, None] <= eff_to[None, :]) | open_ended[None, :]
    active = started & not_ended  # (n_dates, n_flags) bool

    # Net direction = sum of Direction over active flags. We avoid matmul (which
    # raised spurious divide-by-zero/overflow RuntimeWarnings on this BLAS build)
    # and instead mask-and-sum: zero-out inactive flags' directions, sum per row.
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


# CBSL macro-series vintage features (P3). Each maps a MacroSeriesPoints
# SeriesCode -> the feature column name attached at observation time. National
# signal -> identical across crops for a given ObservationDate (no cross-
# sectional lift expected; added on leakage-safety + domain prior, not CV proof).
#
# CCPI_HEADLINE_YOY_BASE2021 is deliberately EXCLUDED: the spec lists three
# columns and headline YoY carries no cross-sectional signal beyond food YoY on
# this thin corpus -- not worth a 4th column. CCPI_BASE2021 (the raw index level)
# is base-year-dependent (2013=100 vs 2021=100 rebasing splices silently) and is
# NOT a feature -- YoY series are base-invariant, so we consume those instead.
_MACRO_SERIES = {
    "CCPI_FOOD_YOY_BASE2021": "MacroFoodInflationYoY",
    "FOOD_IMPORTS_YOY": "MacroFoodImportsYoY",
    "POLICY_RATE_OPR": "MacroPolicyRateOPR",
}
_MACRO_FEATURES = list(_MACRO_SERIES.values())

# Staleness cap (user decision, 2026-07-04): a carried-forward monthly vintage
# is only trusted for ~60 days. If the most recent vintage whose PublishedAt <=
# ObservationDate is itself OLDER than this many days at the observation date,
# the feature is set to NaN rather than carried forward indefinitely. Monthly
# series carried forward uncapped would assert a stale reading is still current;
# NaN ("not knowable / too stale") is the honest value. FX/sentiment carry
# forward uncapped -- fine for ~daily series, wrong for these monthly ones.
_MACRO_STALENESS_DAYS = 60


def _attach_macro(result: pd.DataFrame, macro: pd.DataFrame | None) -> pd.DataFrame:
    """CBSL macro-series columns via point-in-time (as-of, backward) merge on the
    VINTAGE date (PublishedAt), per SeriesCode.

    Mirrors _attach_fx, with three deliberate differences dictated by the P3
    vintage spec:

      1. JOIN KEY = PublishedAt, NEVER ReferenceDate. ReferenceDate is the period
         the value describes; PublishedAt is when the world could know it. A
         backward as-of on PublishedAt (ObservationDate >= PublishedAt) is the
         leakage gate. ReferenceDate is audit-only and dropped before the frame.

      2. STALENESS CAP: after the as-of match, if PublishedAt is more than
         _MACRO_STALENESS_DAYS before ObservationDate the value is NaNed. Monthly
         series must not be carried forward forever (contrast FX/sentiment).

      3. MISSING / ABSENT -> NaN, NEVER 0. A macro NaN means "not knowable at D"
         (before the first vintage, or too stale, or the series is absent),
         deliberately UNLIKE _attach_policy where 0 means "no active policy".

    National signal -> identical across crops for a given ObservationDate.
    Empty/missing macro frame -> all macro columns NaN.
    """
    out = result.copy()
    out["ObservationDate"] = _canon_key(out["ObservationDate"])
    if macro is None or macro.empty:
        for col in _MACRO_FEATURES:
            out[col] = np.nan
        return out

    # as-of requires a sorted LEFT key, so the frame is re-sorted by
    # ObservationDate and returned in that order (same behaviour as
    # _attach_fx/_attach_sentiment; downstream keys on (CropId, ObservationDate)).
    out = out.sort_values("ObservationDate").reset_index(drop=True)

    for series_code, feat_col in _MACRO_SERIES.items():
        sub = macro[macro["SeriesCode"] == series_code]
        # ReferenceDate is NEVER a join key -- carried only for tests/audit, not
        # merged in. We select PublishedAt (the vintage/knowledge date) + Value.
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
        # No match at all (NaT pub) or too stale -> NaN. Operator is STRICT ">":
        # exactly 60 days old is still visible; older than 60 is NaN.
        too_stale = np.isnat(pub) | (age_days > _MACRO_STALENESS_DAYS)
        vals = np.where(too_stale, np.nan, vals)
        out[feat_col] = vals

    return out


# --- Cross-market spread features (P4 step 2, ClickUp 86caheffr) --------------
#
# Per-market point-in-time price context onto the per-(crop, date) frame. These
# are cross-market FEATURES, not a market-as-label reshape (that is P5). The
# whole thing is computed OFFLINE into CropFeatureDaily; serving keeps reading
# ONE row per (crop, date) -- there is NO per-request market fan-out.
#
# Column naming: per-market columns are Mkt<Slug>AvgPrice / Mkt<Slug>Lag7 for
# each feature-safe market slug (Dambulla, Keppetipola, Thambuttegama, Pettah,
# Narahenpita). A market with no data for a (crop, date) -- Poya/gap, or
# Narahenpita which has zero rows today -- gets NaN (never 0): explicit
# missingness. Only the 4 model crops with PriceObservations coverage
# (Capsicum, Bitter Gourd, Ridge Gourd, Lady's Fingers) get non-NaN values; the
# other 7 model crops are NaN by construction (no cross-market observations).
#
# "National" = UNWEIGHTED mean over the per-market AvgPrice columns that report
# on that (crop, date) -- exactly the get_feature_safe_market_ids() set, never a
# raw AVG over PriceObservations (Pettah/ECOMAP double-count trap). NaN (not 0)
# when fewer than 2 markets report -> spread/rank features are NaN too.
#
# Reference market for the crop-vs-national spread = DAMBULLA, the primary
# Dedicated Economic Centre whose price effectively anchors the harvest-price
# label. spread_vs_national / market_rank_pct describe Dambulla's position among
# the reporting feature-safe markets. (There is deliberately no "self market"
# axis here -- that arrives with the market-as-label reshape in P5.)
_SPREAD_REFERENCE_SLUG = "Dambulla"

# Staleness cap for a carried-forward per-market price (days). Measured from the
# live PriceObservations reporting-gap distribution over the 4 covered model
# crops (34,138 consecutive-day gaps): p99.5 = 7d, p99.9 = 29d, 99.79% of gaps
# <= 14d. A daily market series that has not reported a crop for >14 days has
# effectively STOPPED reporting it (not a normal weekend/Poya gap), so carrying
# its last price forward past 14 days would assert a stale price is still
# current. Beyond the cap -> NaN ("not knowable"), never carried forever. This
# is the daily-series analogue of _attach_macro's 60d cap for monthly vintages.
_SPREAD_STALENESS_DAYS = 14

# Non-per-market spread columns (fixed names; the per-market Mkt<Slug>* columns
# are generated from the feature-safe slug list).
_SPREAD_DERIVED_COLS = [
    "SpreadVsNational",
    "MarketRankPct",
    "LeaderMarketLag7",
    "NMarketsReporting",
]


def _asof_market_price(result: pd.DataFrame, sub: pd.DataFrame,
                       left_date_col: str, out_col: str) -> pd.Series:
    """Per-(crop) backward as-of merge of one market's daily AvgPrice onto result.

    For each result row take the most recent AvgPrice of this market for the SAME
    CropId with ObservedDate <= result[left_date_col]. Applies the staleness cap:
    if the matched observation is more than _SPREAD_STALENESS_DAYS before the
    left date, the value is NaN. Pure point-in-time: never an ObservedDate AFTER
    the left date. Returns a Series aligned to result's (already sorted) index.
    """
    n = len(result)
    if sub.empty:
        return pd.Series(np.full(n, np.nan), index=result.index)
    # Carry result's own index as a column so the as-of output can be realigned
    # to the caller's row order (merge_asof requires a globally sorted left key
    # and resets the index).
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
    """Cross-market spread features via per-market point-in-time as-of merges.

    Mirrors _attach_fx/_attach_macro's point-in-time discipline, extended to a
    SET of markets. For each feature-safe market slug we as-of-merge (backward,
    per CropId, staleness-capped) that market's daily AvgPrice at the observation
    date (and 7 days earlier for the Lag7 leg). All derived columns
    (spread/rank/leader/count) come from these already-as-of'd per-market columns
    ONLY -- never from a fresh query -- so they inherit the same leakage gate.

    Empty/missing PriceObservations (or no feature-safe markets) -> every spread
    column NaN (per-market and derived). NaN, never 0: a missing market price is
    "not knowable", not "zero rupees".
    """
    out = result.copy()
    out["ObservationDate"] = _canon_key(out["ObservationDate"])
    slugs = list(market_slugs) if market_slugs else []

    # Per-market AvgPrice + 7-day-lagged AvgPrice columns (one pair per market).
    avg_cols: list[str] = []
    lag_cols: list[str] = []
    if price_obs is None:
        price_obs = pd.DataFrame(columns=["MarketSlug", "CropId", "ObservedDate", "AvgPrice"])

    # Pre-compute the +7d observation date once (point-in-time: the price known
    # 7 days before D -- backward as-of onto D-7 keeps the same leakage gate).
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

    # --- Derived cross-market summaries (from per-market columns ONLY) ---------
    if avg_cols:
        avg_mat = out[avg_cols].to_numpy(dtype="float64")  # (n_rows, n_markets)
    else:
        avg_mat = np.empty((len(out), 0), dtype="float64")

    n_reporting = np.sum(~np.isnan(avg_mat), axis=1) if avg_mat.shape[1] else \
        np.zeros(len(out), dtype="int64")
    out["NMarketsReporting"] = n_reporting.astype("int64")

    # National = unweighted mean over reporting markets; NaN when <2 report.
    # nanmean of an all-NaN row (a non-covered crop) legitimately yields NaN but
    # warns "Mean of empty slice" -- silence it, the NaN is the intended value.
    enough = n_reporting >= 2
    if avg_mat.shape[1]:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", category=RuntimeWarning)
            national = np.nanmean(avg_mat, axis=1)
    else:
        national = np.full(len(out), np.nan)
    national = np.where(enough, national, np.nan)

    # spread_vs_national + rank use the REFERENCE market (Dambulla). Both are NaN
    # when the reference market did not report, or when <2 markets report.
    ref_col = f"Mkt{_SPREAD_REFERENCE_SLUG}AvgPrice"
    ref = out[ref_col].to_numpy(dtype="float64") if ref_col in out.columns \
        else np.full(len(out), np.nan)

    spread = np.where(enough, ref - national, np.nan)
    out["SpreadVsNational"] = spread

    # market_rank_pct: fraction of reporting markets with AvgPrice <= reference,
    # i.e. the reference market's percentile position among reporting markets.
    # Vectorised over rows; only rows with >=2 reporting markets AND a reporting
    # reference get a value.
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

    # leader_market_lag7: the 7-day-lagged AvgPrice of whichever market has the
    # HIGHEST current AvgPrice on this (crop, date). Point-in-time (both legs are
    # as-of'd). NaN when <2 markets report or the leader has no Lag7.
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

    Harvest-anchored + observation-anchored columns (see _festival_features).
    National signal -> identical across crops for a given (ObservationDate,
    HarvestDate). Empty/missing calendar -> every festival column zero-filled
    (there is genuinely no festival signal, so 0 is correct, not NaN). Pure
    calendar function: no now()/today(), no price aggregates.
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
    # Macro vintages attach after policy: both are national point-in-time signals,
    # but macro is an as-of merge on the VINTAGE date (PublishedAt) with a
    # staleness cap and NaN (never 0) for not-knowable, unlike policy's 0-fill.
    result = _attach_macro(result, macro)
    result = _attach_festivals(result, festivals)
    # Cross-market spread context (P4 step 2): per-market point-in-time prices +
    # derived spread/rank/leader summaries, computed OFFLINE into the store.
    result = _attach_market_spread(result, price_obs, market_slugs)
    return result
