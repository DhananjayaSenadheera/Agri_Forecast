"""Stage A -- EXPORT (production venv, pandas 3.0.3).  EVALUATION-ONLY.

Writes CSVs to the scratchpad sandbox so Stage B (sandbox venv, pandas 2.2.x,
prophet) can run WITHOUT importing agriforecast_ml or touching the DB. Exports:
  1. per-crop daily AvgPrice series (ds, y) from load_prices()  -> series_<code>.csv
  2. the Prophet holidays frame                                 -> holidays.csv
  3. the walk-forward origin schedule per crop with, per origin and per horizon
     h in {gp, 7, 30}: target date, shared y_true, seasonal-naive yhat,
     carry-forward yhat                                         -> origins_<code>.csv
  4. the v13 comparator prediction per origin at h=gp, computed POINT-IN-TIME
     (per-origin refit of the pooled hybrid on rows knowable at the origin).

Protocol: PLAN.md (experiments/prophet_spike/PLAN.md). Nothing is committed,
promoted, or written to the registry. Read-only DB.
"""
from __future__ import annotations

import json
import os
import sys
import time

import numpy as np
import pandas as pd

from agriforecast_ml.load import load_prices, load_festivals, to_prophet_holidays
from agriforecast_ml.train import dataset
from agriforecast_ml.train import model as M

SANDBOX = ("/private/tmp/claude-501/-Users-dhananjayasenadheera-Projects-Agri-"
           "Forecast/36778f71-aa24-42a1-8085-3a7f69884e49/scratchpad/"
           "prophet-sandbox")
DATA = os.path.join(SANDBOX, "data")

# Owner-approved crops + matched horizons (gp). gp DEFINES the label + horizon.
CROPS = [
    {"name": "Capsicum", "code": "VEG000015", "gp": 75},
    {"name": "Ridge Gourd", "code": "VEG000057", "gp": 65},
]
HORIZONS = {"gp": None, "7": 7, "30": 30}  # gp filled per-crop below

TEST_FRAC = 0.40         # test region = last 40% of the origin universe
N_FOLDS = 3
ORIGIN_STEP_DAYS = 7     # weekly origins
SNAIVE_TOL_DAYS = 30     # backward-asof tolerance for the y(target-365d) lookup
TARGET_TOL_DAYS = 7      # backward-asof tolerance for y_true (matches store label)
SEED = 42

SPLICE_DATE = pd.Timestamp("2025-05-05")  # HARTI -> Dambulla-DEC source splice


def _asof_backward(series: pd.Series, when: pd.Timestamp, tol_days: int):
    """Nearest observed value at or before `when`, within tol_days. series is a
    date-indexed (sorted) float Series. Returns (value|nan, gap_days|nan)."""
    idx = series.index
    pos = idx.searchsorted(when, side="right") - 1
    if pos < 0:
        return np.nan, np.nan
    d = idx[pos]
    gap = (when - d).days
    if gap > tol_days:
        return np.nan, np.nan
    return float(series.iloc[pos]), int(gap)


def build_origin_universe(feat_dates: np.ndarray) -> tuple[list, list]:
    """Origin universe = the crop's v13 feature ObservationDates (guarantees a
    resolvable v13 label + shared y_true). Test region = last TEST_FRAC, split
    into N_FOLDS sequential blocks; weekly origins within each block."""
    uniq = np.sort(np.unique(feat_dates))
    test_region = uniq[int(len(uniq) * (1.0 - TEST_FRAC)):]
    blocks = [b for b in np.array_split(test_region, N_FOLDS) if len(b)]
    origins, folds = [], []
    for fi, block in enumerate(blocks, 1):
        last = None
        for d in block:
            d = pd.Timestamp(d)
            if last is None or (d - last).days >= ORIGIN_STEP_DAYS:
                origins.append(d)
                folds.append(fi)
                last = d
    return origins, folds


def main() -> None:
    os.makedirs(DATA, exist_ok=True)
    np.random.seed(SEED)
    t_start = time.perf_counter()

    print("=== Stage A export (production venv) ===")
    import pandas as _pd
    print(f"pandas {_pd.__version__}")

    # --- shared: full v13 feature frame + design matrix (fit once, slice per origin)
    df = dataset.load_training_frame()
    X, y, cols = dataset.build_xy(df)
    chash = dataset.contract_hash(cols)
    obs_all = df["ObservationDate"]
    lbl_all = df["HarvestDate"]
    print(f"v13 frame: rows={len(df)} crops={df['CropId'].nunique()} "
          f"features={len(cols)} contract_hash={chash}")

    # --- holidays frame
    hol = to_prophet_holidays(load_festivals())
    hol_out = hol.copy()
    hol_out["ds"] = pd.to_datetime(hol_out["ds"]).dt.strftime("%Y-%m-%d")
    hol_out.to_csv(os.path.join(DATA, "holidays.csv"), index=False)
    print(f"holidays.csv rows={len(hol_out)}")

    ph = load_prices()

    summary = {"contract_hash": chash, "crops": []}

    for spec in CROPS:
        name, code, gp = spec["name"], spec["code"], spec["gp"]
        print(f"\n--- {name} ({code}) gp={gp} ---")

        # daily series (ds,y) -- the exact series v13's label derives from
        g = ph[ph["CropCode"] == code][["PriceDate", "AvgPrice"]].copy()
        g = g.drop_duplicates("PriceDate").sort_values("PriceDate")
        s = pd.Series(g["AvgPrice"].to_numpy(dtype=float),
                      index=pd.DatetimeIndex(g["PriceDate"].astype("datetime64[ns]")))
        s = s.sort_index()
        ser_out = pd.DataFrame({"ds": s.index.strftime("%Y-%m-%d"), "y": s.values})
        ser_out.to_csv(os.path.join(DATA, f"series_{code}.csv"), index=False)
        print(f"series_{code}.csv rows={len(ser_out)} "
              f"range {s.index.min().date()}..{s.index.max().date()}")

        # v13 feature rows for this crop (origin universe source)
        cm = (df["CropCode"] == code).to_numpy()
        feat_dates = df.loc[cm, "ObservationDate"].to_numpy()
        origins, folds = build_origin_universe(feat_dates)
        print(f"origins={len(origins)}  folds={dict(zip(*np.unique(folds, return_counts=True)))}")

        # per-origin build
        recs = []
        v13_fit_cache: dict = {}
        for t0, fold in zip(origins, folds):
            rec = {"crop_code": code, "crop_name": name, "gp": gp,
                   "fold": fold, "origin": t0.strftime("%Y-%m-%d")}
            # carry-forward reference = y(origin)
            y_org, _ = _asof_backward(s, t0, 0)  # exact (origin IS an observed date)
            rec["y_origin"] = y_org
            rec["origin_post_splice"] = int(t0 >= SPLICE_DATE)

            for hk, hd in (("gp", gp), ("7", 7), ("30", 30)):
                target = t0 + pd.Timedelta(days=hd)
                yt, ytgap = _asof_backward(s, target, TARGET_TOL_DAYS)
                sn, sngap = _asof_backward(s, target - pd.Timedelta(days=365), SNAIVE_TOL_DAYS)
                rec[f"target_{hk}"] = target.strftime("%Y-%m-%d")
                rec[f"y_true_{hk}"] = yt
                rec[f"y_true_gap_{hk}"] = ytgap
                rec[f"snaive_{hk}"] = sn
                rec[f"snaive_gap_{hk}"] = sngap
                rec[f"carry_{hk}"] = y_org
                rec[f"target_post_splice_{hk}"] = int(target >= SPLICE_DATE)

            # --- v13 point-in-time prediction at h=gp -----------------------
            # LEAKAGE GATE: train only on rows knowable at t0 -> ObservationDate
            # < t0 (no row at/after origin) AND HarvestDate < t0 (purge: fully
            # resolved labels only). Fit pooled model on history-gated crops
            # (the v13 served set); predict this crop's row at ObservationDate==t0.
            tr = (obs_all < t0).to_numpy() & (lbl_all < t0).to_numpy()
            key = t0.normalize()
            gated = M.history_gated_crops(df[tr])
            crop_gated = df.loc[cm, "CropId"].iloc[0] in gated
            rec["v13_crop_gated"] = int(crop_gated)
            # the (crop, t0) feature row to score
            row_mask = cm & (obs_all == t0).to_numpy()
            if crop_gated and row_mask.any():
                gmask = tr & df["CropId"].isin(gated).to_numpy()
                if key in v13_fit_cache:
                    mdl, max_obs = v13_fit_cache[key]
                else:
                    Xg, yg = X[gmask], y[gmask]
                    mdl = M.make_model(0.5)
                    mdl.fit(Xg, np.log1p(yg))
                    max_obs = obs_all[tr].max()
                    v13_fit_cache[key] = (mdl, max_obs)
                pred = float(np.expm1(mdl.predict(X[row_mask]))[0])
                rec["v13_gp"] = pred
                rec["v13_train_rows"] = int(gmask.sum())
                # leakage self-check: no training row at/after origin
                rec["v13_max_train_obs"] = pd.Timestamp(max_obs).strftime("%Y-%m-%d")
                rec["v13_leak_ok"] = int(pd.Timestamp(max_obs) < t0)
            else:
                rec["v13_gp"] = np.nan
                rec["v13_train_rows"] = 0
                rec["v13_max_train_obs"] = ""
                rec["v13_leak_ok"] = 1  # no fit -> vacuously ok
            recs.append(rec)

        odf = pd.DataFrame(recs)
        odf.to_csv(os.path.join(DATA, f"origins_{code}.csv"), index=False)
        n_v13 = int(odf["v13_gp"].notna().sum())
        n_snaive_gp = int(odf["snaive_gp"].notna().sum())
        n_ytrue_gp = int(odf["y_true_gp"].notna().sum())
        leak_bad = int((odf["v13_leak_ok"] == 0).sum())
        print(f"origins_{code}.csv rows={len(odf)}  y_true(gp) def={n_ytrue_gp}  "
              f"snaive(gp) def={n_snaive_gp}  v13 def={n_v13}  v13_leak_bad={leak_bad}")
        summary["crops"].append({
            "name": name, "code": code, "gp": gp,
            "series_rows": int(len(ser_out)),
            "series_start": str(s.index.min().date()),
            "series_end": str(s.index.max().date()),
            "n_origins": int(len(odf)),
            "fold_counts": {int(k): int(v) for k, v in
                            zip(*np.unique(folds, return_counts=True))},
            "n_v13_defined": n_v13, "n_snaive_gp_defined": n_snaive_gp,
            "n_ytrue_gp_defined": n_ytrue_gp, "v13_leak_bad": leak_bad,
        })

    summary["wall_time_s"] = round(time.perf_counter() - t_start, 1)
    with open(os.path.join(DATA, "export_summary.json"), "w") as f:
        json.dump(summary, f, indent=2)
    print(f"\n=== Stage A done in {summary['wall_time_s']}s -> {DATA} ===")


if __name__ == "__main__":
    main()
