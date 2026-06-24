"""QA harness for the feature pipeline. Independently recomputes features and
runs a leakage-by-truncation test (the decisive no-leakage check)."""
from __future__ import annotations

import numpy as np
import pandas as pd

from agriforecast_ml import load, features
from agriforecast_ml.db import get_engine

FF = 5  # must match features._FFILL_LIMIT


def daily_price(group):
    g = group.sort_values("PriceDate").set_index("PriceDate")
    full = pd.date_range(g.index.min(), g.index.max(), freq="D")
    return g["AvgPrice"].reindex(full).ffill(limit=FF)


def main():
    passed, failed = [], []
    def check(name, ok, detail=""):
        (passed if ok else failed).append(name)
        print(f"  [{'PASS' if ok else 'FAIL'}] {name}" + (f" — {detail}" if detail else ""))

    eng = get_engine()
    feats = pd.read_sql("SELECT * FROM CropFeatureDaily", eng)
    feats["ObservationDate"] = pd.to_datetime(feats["ObservationDate"])
    prices = load.load_prices()

    print("=== TC1: table sanity ===")
    check("row count > 0", len(feats) > 0, f"{len(feats)} rows")
    check("41 columns (40 features + ComputedAtUtc)", feats.shape[1] == 41, f"{feats.shape[1]} cols")
    check("no duplicate (crop,date) keys",
          not feats.duplicated(["CropId", "ObservationDate"]).any())

    # --- Brinjal as the worked example ---
    brinjal = prices[prices["CropName"] == "Brinjal"]
    bcrop = brinjal["CropId"].iloc[0]
    dp = daily_price(brinjal)
    fb = feats[feats["CropId"] == bcrop].set_index("ObservationDate").sort_index()

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

    print("\n=== TC3: label correctness (Brinjal gp=90) ===")
    for d in samples:
        hd = d + pd.Timedelta(days=90)
        exp_label = dp.get(hd, np.nan)
        got_label = fb.loc[d, "LabelHarvestPrice"]
        ok = (pd.isna(got_label) and pd.isna(exp_label)) or (pd.notna(got_label) and abs(got_label - exp_label) < 0.01)
        check(f"Label @ {d.date()} -> {hd.date()}", ok, f"db={got_label} exp={exp_label}")

    print("\n=== TC4: weather point-in-time (uses last COMPLETE month M-1) ===")
    weather = load.load_weather()
    wmap = {r.Month: (r.AvgTemperature, r.TotalRainfall) for r in weather.itertuples()}
    for d in samples:
        cur = d.to_period("M") - 1
        exp_t = wmap.get(cur, (np.nan, np.nan))[0]
        got_t = fb.loc[d, "WxAvgTempC"]
        ok = (pd.isna(got_t) and pd.isna(exp_t)) or (pd.notna(got_t) and abs(got_t - exp_t) < 0.01)
        check(f"WxAvgTempC @ {d.date()} = wx[{cur}]", ok, f"db={got_t} exp={exp_t}")

    print("\n=== TC5: LEAKAGE-BY-TRUNCATION (decisive test) ===")
    # Rebuild Brinjal features twice: full data vs data truncated at cutoff C.
    # Features for dates well before C MUST be identical — if any used future
    # data, truncation would change them.
    crops = load.load_crops(); wx = load.load_weather()
    cutoff = pd.Timestamp("2026-01-15")
    full = features.build_all(brinjal, crops, wx)
    trunc_src = brinjal[brinjal["PriceDate"] <= cutoff]
    trunc = features.build_all(trunc_src, crops, wx)
    feat_cols = [c for c in full.columns if c not in
                 ("CropId","CropCode","CropName","ObservationDate","HarvestDate",
                  "LabelHarvestPrice","LabelAvailable")]  # label legitimately uses future
    fa = full.set_index("ObservationDate")[feat_cols]
    ta = trunc.set_index("ObservationDate")[feat_cols]
    # compare dates at least 1 window (90d) before cutoff
    safe = ta.index[ta.index <= cutoff - pd.Timedelta(days=90)]
    diff = (fa.loc[safe] - ta.loc[safe]).abs()
    max_diff = np.nanmax(diff.values)
    check("features identical with/without future data (max abs diff < 1e-6)",
          max_diff < 1e-6, f"max diff={max_diff:.2e} over {len(safe)} dates x {len(feat_cols)} feats")

    print(f"\n=== RESULT: {len(passed)} passed, {len(failed)} failed ===")
    if failed:
        print("FAILED:", failed)


if __name__ == "__main__":
    main()
