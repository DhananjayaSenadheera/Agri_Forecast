"""Stage B -- PROPHET FITS (sandbox venv, pandas 2.2.x + prophet).  EVAL-ONLY.

Reads ONLY the Stage-A CSVs (no DB, no agriforecast_ml import). Per PLAN.md:
per-origin refit on ds < origin, holidays-only (the exported frame; NO built-in
country holidays, NO extra regressors), MAP (mcmc_samples=0), seed 42, hard
L-BFGS iteration cap. Forecasts y at origin+{gp,7,30} per origin and writes
per_origin_<code>.csv back into the sandbox data dir.
"""
from __future__ import annotations

import json
import os
import time
import warnings

import numpy as np
import pandas as pd

warnings.filterwarnings("ignore")

SANDBOX = ("/private/tmp/claude-501/-Users-dhananjayasenadheera-Projects-Agri-"
           "Forecast/36778f71-aa24-42a1-8085-3a7f69884e49/scratchpad/"
           "prophet-sandbox")
DATA = os.path.join(SANDBOX, "data")
SEED = 42
ITER_CAP = 10000

# cmdstan quirk (PLAN sec 6): prophet 1.1.7 bundles a stripped cmdstan lacking a
# top-level makefile that cmdstanpy>=1.3 validates; touch an empty one (venv-local,
# binary precompiled so make is never invoked) and point CMDSTAN at it.
if not os.environ.get("CMDSTAN"):
    import prophet as _pp
    _bundled = os.path.join(os.path.dirname(_pp.__file__),
                            "stan_model", "cmdstan-2.33.1")
    if os.path.isdir(_bundled):
        _mk = os.path.join(_bundled, "makefile")
        if not os.path.exists(_mk):
            open(_mk, "a").close()
        os.environ["CMDSTAN"] = _bundled

from prophet import Prophet  # noqa: E402

CROPS = [("Capsicum", "VEG000015", 75), ("Ridge Gourd", "VEG000057", 65)]


def fit_one(train: pd.DataFrame, holidays: pd.DataFrame) -> Prophet:
    """Default Prophet + holidays-only, MAP, seeded, hard L-BFGS iter cap."""
    m = Prophet(holidays=holidays, mcmc_samples=0)  # defaults for changepoint/seasonality
    try:
        m.fit(train, seed=SEED, iter=ITER_CAP, algorithm="LBFGS")
    except TypeError:
        # older/newer backend that doesn't accept iter/algorithm kwargs
        m.fit(train, seed=SEED)
    return m


def main() -> None:
    import pandas as _pd
    import prophet as _pp
    import cmdstanpy as _cs
    print("=== Stage B Prophet fits (sandbox venv) ===")
    print(f"pandas {_pd.__version__}  prophet {_pp.__version__}  "
          f"cmdstanpy {_cs.__version__}  numpy {np.__version__}")

    np.random.seed(SEED)
    hol = pd.read_csv(os.path.join(DATA, "holidays.csv"))
    hol["ds"] = pd.to_datetime(hol["ds"])

    t_start = time.perf_counter()
    stats = {"crops": []}

    for name, code, gp in CROPS:
        print(f"\n--- {name} ({code}) gp={gp} ---")
        s = pd.read_csv(os.path.join(DATA, f"series_{code}.csv"))
        s["ds"] = pd.to_datetime(s["ds"])
        s["y"] = s["y"].astype(float)
        origins = pd.read_csv(os.path.join(DATA, f"origins_{code}.csv"))

        rows = []
        leak_bad = 0
        seas_seen: dict = {}
        for _, o in origins.iterrows():
            t0 = pd.Timestamp(o["origin"])
            train = s[s["ds"] < t0][["ds", "y"]].copy()
            # leakage assert: no training row at/after the origin
            assert train["ds"].max() < t0, f"LEAK: train max {train['ds'].max()} >= origin {t0}"
            m = fit_one(train, hol)
            active = tuple(sorted(m.seasonalities.keys()))
            seas_seen[active] = seas_seen.get(active, 0) + 1

            fut = pd.DataFrame({"ds": pd.to_datetime(
                [o["target_gp"], o["target_7"], o["target_30"]])})
            fc = m.predict(fut).set_index("ds")["yhat"]
            rows.append({
                "origin": o["origin"],
                "prophet_gp": float(fc.iloc[0]),
                "prophet_7": float(fc.iloc[1]),
                "prophet_30": float(fc.iloc[2]),
                "prophet_train_rows": int(len(train)),
                "prophet_train_max_ds": train["ds"].max().strftime("%Y-%m-%d"),
                "prophet_leak_ok": int(train["ds"].max() < t0),
                "prophet_seasonalities": "|".join(active),
            })
        out = pd.DataFrame(rows)
        leak_bad = int((out["prophet_leak_ok"] == 0).sum())
        out.to_csv(os.path.join(DATA, f"prophet_{code}.csv"), index=False)
        print(f"prophet_{code}.csv rows={len(out)}  leak_bad={leak_bad}  "
              f"seasonalities={ {'|'.join(k): v for k, v in seas_seen.items()} }")
        stats["crops"].append({
            "name": name, "code": code, "n_origins": int(len(out)),
            "leak_bad": leak_bad,
            "seasonalities": {"|".join(k): v for k, v in seas_seen.items()},
        })

    stats["wall_time_s"] = round(time.perf_counter() - t_start, 1)
    with open(os.path.join(DATA, "prophet_summary.json"), "w") as f:
        json.dump(stats, f, indent=2)
    print(f"\n=== Stage B done in {stats['wall_time_s']}s ===")


if __name__ == "__main__":
    main()
