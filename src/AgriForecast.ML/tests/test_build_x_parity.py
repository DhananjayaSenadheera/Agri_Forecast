"""
AgriForecast ML -- R1.1 P4 step 0a: single-source serve-time input frame.

`serving/predict.py` and `serving/explain.py` used to each carry a byte-for-byte
copy of `_build_X`, kept in lock-step by hand. They now both delegate to the one
implementation in `serving/build_x.py`. These tests pin:

  1. Both call sites resolve to the SAME function object (no silent second copy).
  2. That single builder produces the identical DataFrame -- values AND dtypes --
     for a representative row: a categorical column, a NaN-carrying numeric
     column, and a feature_col whose key is MISSING from the row (must coerce to
     NaN, never 0.0 -- the deliberate serving NaN discipline).

No DB, no network, no model artifacts -- pure coercion contract.
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

from agriforecast_ml.serving import explain  # noqa: E402
from agriforecast_ml.serving.build_x import build_x  # noqa: E402


# Representative payload: a categorical col (CropId), several numerics, one of
# which the row will carry as NaN, and one (MacroFoodInflationYoY) that the row
# omits entirely so the missing-key -> NaN path is exercised.
_FEATURE_COLS = [
    "CropId",            # categorical
    "AvgPrice",          # populated numeric
    "MeanSentiment",     # NaN-carrying numeric
    "MacroFoodInflationYoY",  # MISSING from row entirely
]
_PAYLOAD = {"feature_cols": _FEATURE_COLS, "categorical": ["CropId"]}

_ROW = {
    "CropId": "abc-123",
    "AvgPrice": 100.0,
    "MeanSentiment": np.nan,
    # MacroFoodInflationYoY deliberately absent
}


def test_explain_build_x_is_the_single_source():
    """explain._build_X must BE the shared build_x, not a re-derived copy."""
    assert explain._build_X is build_x


def test_predict_build_x_delegates_to_single_source():
    """predict._build_X must route through the shared build_x for its payload."""
    import agriforecast_ml.serving.predict as P

    # Other suites stub serving.predict in sys.modules (to keep app import off the
    # DB); when that stub is loaded the real module isn't present to introspect.
    if not hasattr(P, "build_x"):
        pytest.skip("serving.predict is stubbed by another test module")

    orig = P.build_x
    captured = {}

    def _spy(row, payload):
        captured["row"] = row
        captured["payload"] = payload
        return orig(row, payload)

    P.build_x = _spy
    try:
        P._PAYLOAD = _PAYLOAD  # deterministic payload for the delegation check
        out = P._build_X(_ROW)
    finally:
        P.build_x = orig

    assert captured["payload"] is _PAYLOAD
    # predict passes its module payload; explain passes an explicit one -- same fn.
    expected = build_x(_ROW, _PAYLOAD)
    pd.testing.assert_frame_equal(out, expected)


def test_build_x_values_and_dtypes():
    """The single builder: categorical dtype, float64 numerics, NaN preserved for
    both an explicit NaN and a missing key (never 0.0)."""
    X = build_x(_ROW, _PAYLOAD)

    assert list(X.columns) == _FEATURE_COLS
    assert str(X["CropId"].dtype) == "category"
    assert X["CropId"].iloc[0] == "abc-123"

    for c in ("AvgPrice", "MeanSentiment", "MacroFoodInflationYoY"):
        assert str(X[c].dtype) == "float64", f"{c} should coerce to float64"

    assert X["AvgPrice"].iloc[0] == 100.0
    assert pd.isna(X["MeanSentiment"].iloc[0]), "explicit NaN must stay NaN"
    assert pd.isna(X["MacroFoodInflationYoY"].iloc[0]), (
        "missing key must coerce to NaN, never 0.0")


def test_series_and_dict_rows_agree():
    """A pandas Series row (what predict._latest_feature_row yields) and a dict row
    must build identically -- the builder only relies on .get()."""
    series_row = pd.Series(_ROW)
    X_series = build_x(series_row, _PAYLOAD)
    X_dict = build_x(_ROW, _PAYLOAD)
    pd.testing.assert_frame_equal(X_series, X_dict)
