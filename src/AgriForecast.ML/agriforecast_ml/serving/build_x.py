"""Single source of truth for the serve-time model-input frame.

Both the prediction path (`serving/predict.py`) and the SHAP explanation path
(`serving/explain.py`) must build the *identical* 1-row input DataFrame from a
feature row + the promoted payload. This module is that one implementation;
never re-derive it in either caller (byte-for-byte duplication silently drifted
into train/serve skew in one path but not the other before P4).

Serving contract — missing features stay NaN, never 0.0. A missing sentiment /
macro column (e.g. MeanSentiment / DroughtRatio / FloodRatio / PolicyRatio,
MacroFoodInflationYoY, ...) means "no signal as of this date"; 0.0 is a
*measured* neutral value, which is semantically different. Training leaves these
NaN and XGBoost handles NaN natively (learned default split direction). So
`errors="coerce"` below is deliberate — do NOT add `.fillna(0)`.
"""
from __future__ import annotations

import pandas as pd


def build_x(row, payload) -> pd.DataFrame:
    """Recreate the exact 1-row model input frame, dynamic off payload feature_cols.

    `row` is a mapping (pandas Series or dict) of raw CropFeatureDaily columns.
    Columns in `payload["categorical"]` become pandas ``category`` dtype; every
    other feature column is coerced to float64 with missing values preserved as
    NaN (see module docstring / serving NaN discipline).
    """
    cols = payload["feature_cols"]
    categorical = payload["categorical"]
    X = pd.DataFrame([{c: row.get(c) for c in cols}])
    for c in cols:
        if c in categorical:
            X[c] = X[c].astype("category")
        else:
            X[c] = pd.to_numeric(X[c], errors="coerce").astype("float64")
    return X
