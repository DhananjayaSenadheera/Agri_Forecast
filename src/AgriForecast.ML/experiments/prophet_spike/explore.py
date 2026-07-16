"""Step 1 Prophet-spike exploration (EVALUATION-ONLY, read-only DB).

Runs three probes and prints a structured report:
  A. Stan smoke test  -- trivial Prophet MAP fit, fixed seed, wall time.
  B. to_prophet_holidays scaffold verification (read-only DB).
  C. Crop richness for the daily AvgPrice series (MarketPrices + PriceObservations).

No production code is modified; nothing is promoted or written to the registry.
Run from the ML package root with the venv python.
"""
from __future__ import annotations

import os
import time
import warnings

import numpy as np
import pandas as pd

warnings.filterwarnings("ignore")

# Point cmdstanpy at the cmdstan toolchain bundled inside the prophet wheel so
# no separate `install_cmdstan` is needed (offline, deterministic).
if not os.environ.get("CMDSTAN"):
    import prophet as _pp
    _bundled = os.path.join(os.path.dirname(_pp.__file__),
                            "stan_model", "cmdstan-2.33.1")
    if os.path.isdir(_bundled):
        # prophet 1.1.7's bundled cmdstan is stripped of the top-level `makefile`
        # that cmdstanpy>=1.3 validates; the model binary is precompiled so make is
        # never invoked -- create an empty makefile so validation passes (venv-local).
        _mk = os.path.join(_bundled, "makefile")
        if not os.path.exists(_mk):
            open(_mk, "a").close()
        os.environ["CMDSTAN"] = _bundled


def stan_smoke() -> None:
    print("\n===== (A) STAN SMOKE TEST =====")
    from prophet import Prophet
    import prophet as _p, cmdstanpy as _c
    import os

    print(f"prophet {_p.__version__}  cmdstanpy {_c.__version__}")
    _sm = os.path.join(os.path.dirname(_p.__file__), "stan_model")
    print(f"prophet bundled stan backend dir: {_sm}")
    print(f"  bundled cmdstan: {[d for d in os.listdir(_sm) if d.startswith('cmdstan')]}")
    rng = np.random.default_rng(42)
    n = 100
    ds = pd.date_range("2020-01-01", periods=n, freq="D")
    trend = np.linspace(10.0, 20.0, n)
    season = 3.0 * np.sin(2 * np.pi * np.arange(n) / 30.0)
    y = trend + season + rng.normal(0, 0.5, n)
    df = pd.DataFrame({"ds": ds, "y": y})

    # Trend-only fit: confirms the Stan backend compiles/runs end-to-end.
    t0 = time.perf_counter()
    m = Prophet(weekly_seasonality=False, daily_seasonality=False,
                yearly_seasonality=False)  # default fit == MAP (optimize), NOT MCMC
    m.fit(df, seed=42)
    dt = time.perf_counter() - t0
    fc = m.predict(m.make_future_dataframe(periods=10))
    print(f"prophet.mcmc_samples = {m.mcmc_samples} (0 => MAP/optimize, not MCMC)")
    print(f"stan backend = {m.stan_backend.get_type()}")
    print(f"TREND-ONLY fit+predict rows={n} -> wall time = {dt:.2f}s")
    print(f"forecast tail yhat: {fc['yhat'].tail(3).round(2).tolist()}")

    # Compatibility probe: does ANY seasonality / holidays component work?
    for label, kwargs in [("weekly_seasonality", dict(weekly_seasonality=True)),
                          ("yearly_seasonality", dict(yearly_seasonality=True))]:
        try:
            mm = Prophet(daily_seasonality=False, **{
                "weekly_seasonality": False, "yearly_seasonality": False, **kwargs})
            mm.fit(df, seed=42)
            print(f"  compat {label}: OK")
        except Exception as e:
            print(f"  compat {label}: FAIL -> {type(e).__name__}: {str(e)[:80]}")


def holidays_probe() -> None:
    print("\n===== (B) to_prophet_holidays SCAFFOLD =====")
    from agriforecast_ml.load import load_festivals, to_prophet_holidays

    fest = load_festivals()
    print(f"load_festivals rows: {len(fest)}  cols: {list(fest.columns)}")
    if not fest.empty:
        print(f"festival date range: {fest['Date'].min().date()} .. {fest['Date'].max().date()}")
        print(f"distinct FestivalKey: {sorted(fest['FestivalKey'].unique())}")
        print(f"LeadUpDays value counts:\n{fest['LeadUpDays'].value_counts().to_string()}")
        print(f"IsProvisional true count: {int(fest['IsProvisional'].sum())}")

    hol = to_prophet_holidays(fest)
    print(f"\nto_prophet_holidays rows: {len(hol)}  cols: {list(hol.columns)}")
    if not hol.empty:
        print(f"lower_window range: {hol['lower_window'].min()} .. {hol['lower_window'].max()}")
        print(f"upper_window distinct: {sorted(hol['upper_window'].unique())}")
        print(f"rows with lead-up (lower_window<0): {int((hol['lower_window'] < 0).sum())}")
        # Avurudu paired-day handling: look for Avurudu keys near Apr 13/14
        av = hol[hol["holiday"].str.contains("vurudu|luth|Avurudu|Aluth", case=False, regex=True)]
        print(f"\nAvurudu-like rows: {len(av)}")
        if not av.empty:
            avv = av.copy()
            avv["ds"] = pd.to_datetime(avv["ds"])
            print(avv.assign(month=avv["ds"].dt.month, day=avv["ds"].dt.day)
                    .sort_values("ds")[["holiday", "ds", "lower_window", "upper_window"]]
                    .head(12).to_string(index=False))
        print("\nsample holiday rows:")
        print(hol.head(8).to_string(index=False))


def _cycles(days: float) -> float:
    return round(days / 365.25, 1)


def crop_richness() -> None:
    print("\n===== (C) CROP RICHNESS (daily AvgPrice) =====")
    from agriforecast_ml.load import (load_prices, load_price_observations)

    # --- Source 1: MarketPrices (crop-date blended daily series; HARTI backfill) ---
    print("\n--- Source 1: MarketPrices via load_prices (per crop, blended) ---")
    ph = load_prices()
    print(f"total rows: {len(ph)}  crops: {ph['CropId'].nunique()}  "
          f"date range: {ph['PriceDate'].min().date()}..{ph['PriceDate'].max().date()}")

    def summarize(g: pd.DataFrame, datecol: str) -> pd.Series:
        d = g[datecol].sort_values().drop_duplicates()
        span = (d.max() - d.min()).days + 1
        gaps = d.diff().dt.days.dropna()
        max_gap = int(gaps.max()) if len(gaps) else 0
        n_days = d.nunique()
        pct_missing = round(100.0 * (span - n_days) / span, 1) if span else 0.0
        return pd.Series({
            "n_rows": len(g), "n_days": n_days,
            "start": d.min().date(), "end": d.max().date(),
            "span_days": span, "max_gap_d": max_gap,
            "pct_missing": pct_missing, "cycles": _cycles(span),
        })

    name_map = ph.groupby("CropId")["CropName"].first()
    code_map = ph.groupby("CropId")["CropCode"].first()
    tbl = ph.groupby("CropId").apply(lambda g: summarize(g, "PriceDate"))
    tbl["name"] = tbl.index.map(name_map)
    tbl["code"] = tbl.index.map(code_map)
    tbl = tbl.sort_values("n_days", ascending=False)
    cols = ["code", "name", "n_rows", "n_days", "start", "end", "span_days",
            "max_gap_d", "pct_missing", "cycles"]
    print(tbl[cols].head(12).to_string())

    # --- Source 2: PriceObservations (per market-slug daily series) ---
    print("\n--- Source 2: PriceObservations via load_price_observations (per market) ---")
    po = load_price_observations()
    if po.empty:
        print("PriceObservations empty / unreachable.")
        return
    print(f"total rows: {len(po)}  crops: {po['CropId'].nunique()}  "
          f"markets: {po['MarketSlug'].nunique()}  "
          f"date range: {po['ObservedDate'].min().date()}..{po['ObservedDate'].max().date()}")
    # per (crop, market)
    g2 = po.groupby(["CropId", "MarketSlug"]).apply(lambda g: summarize(g, "ObservedDate"))
    g2 = g2.reset_index()
    g2["name"] = g2["CropId"].map(name_map)
    g2 = g2.sort_values("n_days", ascending=False)
    print("\nTop (crop x market) daily series by day-count:")
    print(g2[["name", "MarketSlug", "n_rows", "n_days", "start", "end",
              "span_days", "max_gap_d", "pct_missing", "cycles"]].head(12).to_string(index=False))

    # cross-market mean series per crop (from PriceObservations)
    print("\nCross-market MEAN series per crop (PriceObservations):")
    xm = (po.groupby(["CropId", "ObservedDate"], as_index=False)["AvgPrice"].mean())
    g3 = xm.groupby("CropId").apply(lambda g: summarize(g, "ObservedDate"))
    g3["name"] = g3.index.map(name_map)
    g3 = g3.sort_values("n_days", ascending=False)
    print(g3[["name", "n_rows", "n_days", "start", "end", "span_days",
              "max_gap_d", "pct_missing", "cycles"]].head(8).to_string())


if __name__ == "__main__":
    stan_smoke()
    holidays_probe()
    crop_richness()
    print("\n===== DONE =====")
