"""QA harness for the feature pipeline. Independently recomputes features and
runs a leakage-by-truncation test (the decisive no-leakage check).

`run_qa()` is invoked from INSIDE build_features.py's post-persist QA gate
(see build_features.py's `_run_qa_gate`), after CropFeatureDaily has been
written but BEFORE the FEATURE_BUILD run row is marked Succeeded. This is
DETECTION, not PREVENTION: build_features.py has already persisted
CropFeatureDaily (if_exists='replace') by the time this runs, so a failed
check means bad features are already live in the store -- it does not block
the write. But a failed check DOES flip that run's FEATURE_BUILD row to
Failed and re-raise (build_features.py fails loudly, same as any other
pipeline exception).

What "visible" means, precisely (verified against AgriForecast.Application/
.../GetIngestionStatusQueryHandler.cs and AgriForecast.Domain/Constants/
IngestionSources.cs, not assumed): the Failed row DOES appear in the admin
ingestion runs table at /admin/ingestion (GET /runs, unfiltered by default,
also filterable by ?source=FEATURE_BUILD). It does NOT flip that page's
status card/banner (state / lastRunAtUtc / lastRunStatus) --
IngestionSources.ExcludedFromServiceState deliberately excludes FEATURE_BUILD
from that aggregate on purpose (the feature build runs last every day, so
without the exclusion it would permanently mask a failed DAMBULLA_DEC /
WEATHER / ECONOMIC / NEWS run behind an evergreen "succeeded" -- the
"lastRunStatus-hijack trap"). There is also no separate ingestion-aware
"health endpoint": the API's literal /health route is a static liveness ping
({"status":"healthy"}) for k8s probes, unrelated to IngestionRuns. So: real,
row-level visibility on the runs page -- not a banner flip, not a health
check.

A crash INSIDE this harness (a bug here, not a deliberately failed check) is
a different case and is fail-open at the build_features.py call site, not
here -- see `_run_qa_gate`'s docstring.

Also runnable standalone for a manual check: `python qa_features.py` runs
the same suite and exits 1 on any failed check, 0 if all pass. This script
does NOT write to IngestionRuns itself -- that only happens through
build_features.py's integration.

KNOWN GAP (not fixed here, see memory agriforecast-qa-features-contract.md):
TC5/TC5b call build_all() WITHOUT festivals/macro/price_obs/market_slugs, so
real leakage coverage is 42 of 76 feature columns. Widening that coverage is
a separate task.
"""
from __future__ import annotations

import sys

import numpy as np
import pandas as pd

from agriforecast_ml import load, features
from agriforecast_ml.db import get_engine
from agriforecast_ml.envfile import load_env_file

FF = 5  # must match features._FFILL_LIMIT


def daily_price(group):
    g = group.sort_values("PriceDate").set_index("PriceDate")
    full = pd.date_range(g.index.min(), g.index.max(), freq="D")
    return g["AvgPrice"].reindex(full).ffill(limit=FF)


def run_qa() -> "tuple[list[str], list[str]]":
    """Runs the full QA check suite once against the live DB and returns
    (passed, failed) lists of check names. Never calls sys.exit -- exit-code
    /run-status policy belongs to the caller: the CLI entry point below
    (main()) turns a non-empty `failed` into a process exit 1; build_features
    .py's `_run_qa_gate` turns it into a Failed FEATURE_BUILD row + a raised
    exception. A genuine infra problem (DB unreachable, a query blowing up)
    still raises out of this function exactly as it always has -- deciding
    whether THAT is fail-open or fail-shut is also the caller's job.
    """
    passed, failed = [], []
    def check(name, ok, detail=""):
        (passed if ok else failed).append(name)
        print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" — {detail}" if detail else ""))

    load_env_file()  # so a bare `python qa_features.py` works without sourcing .env first
    eng = get_engine()
    feats = pd.read_sql("SELECT * FROM CropFeatureDaily", eng)
    feats["ObservationDate"] = pd.to_datetime(feats["ObservationDate"])
    prices = load.load_prices()
    crops = load.load_crops()

    print("=== TC1: table sanity ===")
    check("row count > 0", len(feats) > 0, f"{len(feats)} rows")
    # A contract check, not a magic count: every column the feature build is DECLARED to emit
    # must be present. The expected list is derived from features.py's own shared constants
    # plus the per-market spread columns generated from the live market slugs, so it comes
    # from the same source of truth as the build and never goes stale the way a hardcoded
    # column count did.
    market_slugs = load.feature_safe_market_slugs()
    market_cols = [f"Mkt{slug}AvgPrice" for slug in market_slugs] + \
                  [f"Mkt{slug}Lag7" for slug in market_slugs]
    expected_cols = set(
        ["CropId", "CropCode", "CropName", "ObservationDate",
         "AvgPrice", "MinPrice", "MaxPrice",
         "RollMean7", "RollMean30", "RollMean90", "RollStd30", "RollStd90",
         "Momentum", "PriceZScore90", "Lag7", "Lag30", "Lag60", "Lag90",
         "PctChange30",
         "WxAvgTempC", "WxRainfallMm", "WxTempLag1", "WxTempLag2",
         "WxRainLag1", "WxRainLag2", "WxRainAnomaly",
         "GrowthPeriodDays", "PlantingSeasonEnc", "HarvestWindowDays",
         "HarvestDate", "LabelHarvestPrice", "LabelAvailable",
         "FxUsdLkr", "ComputedAtUtc"]
        + features.CALENDAR_FEATURE_COLS
        + features.FESTIVAL_FEATURE_COLS
        + features._SENTIMENT_FEATURES
        + features._POLICY_FEATURES
        + features._MACRO_FEATURES
        + features._SPREAD_DERIVED_COLS
        + market_cols
    )
    # The check is SYMMETRIC. Missing columns are the obvious risk, but extra ones are the
    # dangerous ones: train/dataset.py selects model inputs by DENYLIST, so any stray column
    # that lands in CropFeatureDaily (a merge_asof _x/_y leftover, a raw joined date) becomes
    # a model input automatically, and the store is rebuilt with if_exists='replace' so it
    # propagates on the very next daily run.
    db_cols = set(feats.columns)
    missing_cols = expected_cols - db_cols
    extra_cols = db_cols - expected_cols
    check("all contract columns present, none extra (features.py is the source of truth)",
          not missing_cols and not extra_cols,
          f"{feats.shape[1]} cols in DB, {len(expected_cols)} expected"
          f"{', missing=' + str(sorted(missing_cols)) if missing_cols else ''}"
          f"{', extra=' + str(sorted(extra_cols)) if extra_cols else ''}")
    check("no duplicate (crop,date) keys",
          not feats.duplicated(["CropId", "ObservationDate"]).any())

    # Brinjal as the worked example.
    brinjal = prices[prices["CropName"] == "Brinjal"]
    bcrop = brinjal["CropId"].iloc[0]
    dp = daily_price(brinjal)
    fb = feats[feats["CropId"] == bcrop].set_index("ObservationDate").sort_index()
    bmeta = crops.set_index("CropId").loc[bcrop]
    bgp = load.resolve_forecast_gp(bmeta.get("IsVerified"), bmeta["GrowthPeriodDays"])
    assert bgp is not None, "Brinjal has no resolvable GrowthPeriodDays -- check CropAgronomyProfiles"

    print("\n=== TC2: feature recompute (Brinjal, 3 sample dates) ===")
    samples = fb.index[[100, 200, 300]]
    for d in samples:
        exp_rm30 = dp.loc[:d].tail(30).mean()
        exp_lag30 = dp.loc[:d - pd.Timedelta(days=30)].iloc[-1] if (d - pd.Timedelta(days=30)) >= dp.index.min() else np.nan
        got_rm30 = fb.loc[d, "RollMean30"]
        got_lag30 = fb.loc[d, "Lag30"]
        check(f"RollMean30 @ {d.date()}", abs(got_rm30 - exp_rm30) < 0.01, f"db={got_rm30:.2f} exp={exp_rm30:.2f}")
        # lag30 = exact daily price 30 calendar days earlier
        exp_lag30 = dp.get(d - pd.Timedelta(days=30), np.nan)
        check(f"Lag30 @ {d.date()}", (pd.isna(got_lag30) and pd.isna(exp_lag30)) or abs(got_lag30 - exp_lag30) < 0.01,
              f"db={got_lag30} exp={exp_lag30}")

    print(f"\n=== TC3: label correctness (Brinjal gp={bgp}) ===")
    exp_labels = []
    for d in samples:
        hd = d + pd.Timedelta(days=bgp)
        exp_label = dp.get(hd, np.nan)
        exp_labels.append(exp_label)
        got_label = fb.loc[d, "LabelHarvestPrice"]
        ok = (pd.isna(got_label) and pd.isna(exp_label)) or (pd.notna(got_label) and abs(got_label - exp_label) < 0.01)
        check(f"Label @ {d.date()} -> {hd.date()}", ok, f"db={got_label} exp={exp_label}")
    # Teeth guard: the both-NaN branch above would auto-pass without comparing anything, so
    # at least one sample must carry a real, independently recomputed label.
    check("TC3 has a non-NaN expected label (test has teeth, not vacuous both-NaN)",
          any(pd.notna(v) for v in exp_labels),
          f"{sum(pd.notna(v) for v in exp_labels)}/{len(exp_labels)} samples labelled")

    print("\n=== TC4: weather point-in-time (uses last COMPLETE month M-1) ===")
    weather = load.load_weather()
    wmap = {r.Month: (r.AvgTemperature, r.TotalRainfall) for r in weather.itertuples()}
    # The TC2/TC3 sample dates land years before weather coverage starts, where comparing
    # NaN to NaN proves nothing. Pick samples the store itself flags as weather-covered and
    # then check the recompute agrees.
    wx_covered = fb.index[fb["WxAvgTempC"].notna()]
    assert len(wx_covered) > 0, (
        "no Brinjal rows have WxAvgTempC populated -- TC4 cannot select any "
        "weather-covered sample; weather ingestion may be broken")
    wx_samples = wx_covered[[0, len(wx_covered) // 2, -1]]
    exp_temps = []
    for d in wx_samples:
        cur = d.to_period("M") - 1
        exp_t = wmap.get(cur, (np.nan, np.nan))[0]
        exp_temps.append(exp_t)
        got_t = fb.loc[d, "WxAvgTempC"]
        ok = (pd.isna(got_t) and pd.isna(exp_t)) or (pd.notna(got_t) and abs(got_t - exp_t) < 0.01)
        check(f"WxAvgTempC @ {d.date()} = wx[{cur}]", ok, f"db={got_t} exp={exp_t}")
    # Teeth guard, mirroring TC3: at least one sample must have a real, non-NaN expected
    # temperature, or this block is comparing NaN to NaN.
    check("TC4 has a non-NaN expected temperature (test has teeth, not vacuous both-NaN)",
          any(pd.notna(v) for v in exp_temps),
          f"{sum(pd.notna(v) for v in exp_temps)}/{len(exp_temps)} samples had weather")

    print("\n=== TC5: LEAKAGE-BY-TRUNCATION — Beans, policy-transition era ===")
    # Beans rather than Brinjal because of data density in the safe window: Brinjal has ~380
    # trading rows before the cutoff with 10 gaps over 30 days, against Beans' 1782 rows with
    # one. Beans also spans 11 years, and the 2023-06-15 cutoff sits inside the 2020-2024
    # policy-transition window, giving 4 distinct policy states in the safe window alone.
    #
    # Strategy: rebuild Beans features twice, on full data and on data truncated at cutoff C.
    # Features for dates in the safe window (at least 90 days before C) MUST be bit-identical;
    # any deviation means a feature read future data.
    #
    # NewsSentimentDaily is empty in the DB, so the sentiment as-of merge is never exercised
    # on real data; TC5b below covers it with synthetic data.
    wx = load.load_weather()
    fx = load.load_fx()
    policy = load.load_policy_flags()

    # TC5 main: Beans with real policy data; NewsSentimentDaily is empty.
    beans = prices[prices["CropName"] == "Beans"]
    cutoff = pd.Timestamp("2023-06-15")   # inside 2020-2024 policy-transition window
    safe_end = cutoff - pd.Timedelta(days=90)

    # Use an empty-schema sentiment frame (mirrors the live empty table).
    _SENTIMENT_COLS = ["Date", "MeanSentiment", "ArticleCount",
                       "DroughtRatio", "FloodRatio", "PolicyRatio"]
    sentiment_empty = pd.DataFrame(columns=_SENTIMENT_COLS)

    full = features.build_all(beans, crops, wx, fx,
                              sentiment=sentiment_empty, policy=policy)
    trunc_src = beans[beans["PriceDate"] <= cutoff]
    trunc_fx = fx[fx["date"] <= cutoff] if not fx.empty else fx
    trunc_pol = policy[policy["EffectiveFrom"] <= cutoff]
    trunc = features.build_all(trunc_src, crops, wx, trunc_fx,
                               sentiment=sentiment_empty, policy=trunc_pol)

    # All feature columns (labels excluded — they legitimately look into the future).
    feat_cols = [c for c in full.columns if c not in
                 ("CropId", "CropCode", "CropName", "ObservationDate", "HarvestDate",
                  "LabelHarvestPrice", "LabelAvailable")]
    fa = full.set_index("ObservationDate")[feat_cols]
    ta = trunc.set_index("ObservationDate")[feat_cols]
    safe = ta.index[ta.index <= safe_end]

    # Guard: the safe window must span multiple distinct policy states so that a
    # policy-join leak cannot hide as a constant and slip past the diff check.
    n_policy_states = fa.loc[safe, "ActivePolicyNetDirection"].nunique()
    check("TC5 safe window has >1 distinct ActivePolicyNetDirection (test has teeth)",
          n_policy_states > 1,
          f"{n_policy_states} distinct states in {len(safe)} safe-window rows")

    diff = (fa.loc[safe] - ta.loc[safe]).abs()
    max_diff = float(np.nanmax(diff.values)) if diff.size > 0 else 0.0
    check(
        "TC5 features identical with/without future data — Beans, real policy "
        f"(max abs diff < 1e-6, {len(safe)} dates x {len(feat_cols)} feats)",
        max_diff < 1e-6,
        f"max diff={max_diff:.2e}",
    )

    print("\n=== TC5b: LEAKAGE-BY-TRUNCATION — sentiment backward merge_asof ===")
    # NewsSentimentDaily is empty in the live DB, so TC5 never exercises the sentiment as-of
    # merge. This sub-case builds a small synthetic sentiment frame spanning the cutoff and
    # re-runs the identity check for the 4 sentiment columns. Weekly rows are enough because
    # merge_asof carries the last value forward. Seeded for determinism.
    np.random.seed(42)
    # 30 weekly rows centred on the cutoff: 2023-01-01 to 2023-07-23
    n_sent = 30
    sent_dates = pd.date_range("2023-01-01", periods=n_sent, freq="7D")
    synthetic_sentiment = pd.DataFrame({
        "Date": sent_dates,
        "MeanSentiment": np.random.uniform(-0.5, 0.5, n_sent).round(6),
        "ArticleCount": np.random.randint(5, 50, n_sent),
        "DroughtRatio": np.random.uniform(0.0, 0.4, n_sent).round(6),
        "FloodRatio":   np.random.uniform(0.0, 0.3, n_sent).round(6),
        "PolicyRatio":  np.random.uniform(0.0, 0.5, n_sent).round(6),
    })

    # Build: full uses all synthetic sentiment; truncated uses only rows <= cutoff.
    trunc_sent = synthetic_sentiment[synthetic_sentiment["Date"] <= cutoff]

    full_s = features.build_all(beans, crops, wx, fx,
                                sentiment=synthetic_sentiment, policy=policy)
    trunc_s = features.build_all(trunc_src, crops, wx, trunc_fx,
                                 sentiment=trunc_sent, policy=trunc_pol)

    sent_feat_cols = ["MeanSentiment", "DroughtRatio", "FloodRatio", "PolicyRatio"]
    fa_s = full_s.set_index("ObservationDate")[sent_feat_cols]
    ta_s = trunc_s.set_index("ObservationDate")[sent_feat_cols]
    safe_s = ta_s.index[ta_s.index <= safe_end]

    # Guard: sentiment columns must vary in the safe window (backward merge_asof
    # carried at least 2 distinct synthetic readings into this window).
    n_sent_states = fa_s.loc[safe_s, "MeanSentiment"].dropna().nunique()
    check(
        "TC5b safe window has >1 distinct MeanSentiment value (sentiment path exercised)",
        n_sent_states > 1,
        f"{n_sent_states} distinct values in {len(safe_s)} safe-window rows",
    )

    # Identity check over all 4 sentiment columns.
    sdiff = (fa_s.loc[safe_s] - ta_s.loc[safe_s]).abs()
    max_sdiff = float(np.nanmax(sdiff.values)) if sdiff.size > 0 else 0.0
    check(
        "TC5b sentiment columns identical with/without future data "
        f"(max abs diff < 1e-6, {len(safe_s)} dates x {len(sent_feat_cols)} cols)",
        max_sdiff < 1e-6,
        f"max diff={max_sdiff:.2e}",
    )

    print(f"\n=== RESULT: {len(passed)} passed, {len(failed)} failed ===")
    if failed:
        print("FAILED:", failed)
    return passed, failed


def main() -> int:
    """Standalone CLI entry point: `python qa_features.py`. Runs the same
    checks as the build_features.py integration and maps the result to a
    process exit code -- 1 if any check failed, 0 if all passed -- for a
    manual run or ad-hoc debugging. Does not touch IngestionRuns."""
    _passed, failed = run_qa()
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
