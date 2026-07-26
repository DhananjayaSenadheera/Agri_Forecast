"""
AgriForecast ML -- pandas 3.0 merge_asof datetime-resolution regression tests.

pandas 3.0 made pd.merge_asof strict about join-key dtype resolution. pd.read_sql
returns datetime columns at whatever unit the driver reports (datetime64[s],
datetime64[us], ...) and pd.to_datetime PRESERVES that unit, so a price series
read as [s] and an FX/sentiment series read as [us] have incompatible datetime64
dtypes and the as-of merges in features.py used to raise:

    MergeError: incompatible merge keys [0] dtype('<M8[s]') and
    dtype('<M8[us]'), must be the same type

The fix normalizes every datetime column at the LOAD layer to datetime64[ns]
(load._as_canon_dt). These PURE tests (no DB, no network) pin the behaviour at
the merge sites directly: feed _attach_fx / _attach_sentiment frames whose
datetime columns deliberately carry MISMATCHED resolutions ([s] left vs [us]
right, and vice versa) and assert:
  1. the merge no longer raises,
  2. the backward as-of VALUES are correct, and
  3. the CARDINAL leakage boundary holds: a source date == ObservationDate is
     picked up; a source date one unit AFTER is NOT.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import pandas as pd
import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import features as F  # noqa: E402
from agriforecast_ml import load as L  # noqa: E402


def _obs_frame(dates, unit: str) -> pd.DataFrame:
    """A minimal features-matrix frame with ObservationDate at a given unit."""
    return pd.DataFrame({
        "CropId": ["1"] * len(dates),
        "ObservationDate": pd.to_datetime(pd.Index(dates)).astype(f"datetime64[{unit}]"),
        "AvgPrice": np.arange(len(dates), dtype="float64"),
    })


# _as_canon_dt: the load-layer normalizer collapses every unit to [ns].

@pytest.mark.parametrize("unit", ["s", "ms", "us", "ns"])
def test_as_canon_dt_pins_ns_and_preserves_values(unit: str) -> None:
    raw = pd.Series(pd.to_datetime(
        ["2025-01-01", "2025-06-30", "2025-12-31"]
    )).astype(f"datetime64[{unit}]")
    out = L._as_canon_dt(raw)
    assert out.dtype == np.dtype("datetime64[ns]")
    # Same wall-clock dates -- pure dtype normalization, no value change.
    assert list(out) == list(pd.to_datetime(["2025-01-01", "2025-06-30", "2025-12-31"]))


# _attach_fx: mismatched resolutions must merge AND carry correct as-of values.

@pytest.mark.parametrize("obs_unit,fx_unit", [("s", "us"), ("us", "s"), ("s", "ns")])
def test_attach_fx_survives_mismatched_resolution(obs_unit: str, fx_unit: str) -> None:
    obs = _obs_frame(["2025-01-05", "2025-01-10", "2025-01-20"], obs_unit)
    fx = pd.DataFrame({
        "date": pd.to_datetime(["2025-01-01", "2025-01-10"]).astype(f"datetime64[{fx_unit}]"),
        "fx_usd_lkr": [300.0, 310.0],
    })

    merged = F._attach_fx(obs.copy(), fx)

    # Backward as-of: 01-05 -> 300 (only 01-01 <= it); 01-10 -> 310 (exact match
    # on the boundary is allowed); 01-20 -> 310 (last known carried forward).
    got = merged.sort_values("ObservationDate")["FxUsdLkr"].tolist()
    assert got == [300.0, 310.0, 310.0]


def test_attach_fx_boundary_no_future_leak() -> None:
    """CARDINAL RULE: fx date == ObservationDate is used; one unit AFTER is not."""
    obs = _obs_frame(["2025-03-10"], "s")
    # One fx exactly on D (allowed) and one strictly after D (must be ignored).
    fx = pd.DataFrame({
        "date": pd.to_datetime(["2025-03-10", "2025-03-11"]).astype("datetime64[us]"),
        "fx_usd_lkr": [320.0, 999.0],  # 999 = the "future" trap value
    })
    merged = F._attach_fx(obs.copy(), fx)
    assert merged["FxUsdLkr"].tolist() == [320.0]  # never the after-D value


# _attach_sentiment: identical pattern, same guarantees.

@pytest.mark.parametrize("obs_unit,sent_unit", [("s", "us"), ("us", "s")])
def test_attach_sentiment_survives_mismatched_resolution(obs_unit: str, sent_unit: str) -> None:
    obs = _obs_frame(["2025-02-05", "2025-02-15"], obs_unit)
    sent = pd.DataFrame({
        "Date": pd.to_datetime(["2025-02-01", "2025-02-15"]).astype(f"datetime64[{sent_unit}]"),
        "MeanSentiment": [0.1, 0.5],
        "DroughtRatio": [0.0, 0.2],
        "FloodRatio": [0.0, 0.0],
        "PolicyRatio": [0.0, 0.1],
    })

    merged = F._attach_sentiment(obs.copy(), sent)

    got = merged.sort_values("ObservationDate")["MeanSentiment"].tolist()
    # 02-05 -> 0.1 (only 02-01 <= it); 02-15 -> 0.5 (exact boundary match).
    assert got == [0.1, 0.5]


def test_attach_sentiment_boundary_no_future_leak() -> None:
    obs = _obs_frame(["2025-04-20"], "s")
    sent = pd.DataFrame({
        "Date": pd.to_datetime(["2025-04-20", "2025-04-21"]).astype("datetime64[us]"),
        "MeanSentiment": [0.3, 0.9],  # 0.9 = future trap
        "DroughtRatio": [0.0, 0.0],
        "FloodRatio": [0.0, 0.0],
        "PolicyRatio": [0.0, 0.0],
    })
    merged = F._attach_sentiment(obs.copy(), sent)
    assert merged["MeanSentiment"].tolist() == [0.3]  # never the after-D value
