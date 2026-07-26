"""Single source of truth for the serve-time model-input frame.

Both serving/predict.py and serving/explain.py must build the identical 1-row input
frame from a feature row plus the promoted payload. Never re-derive it in either
caller: that duplication is exactly how train/serve skew got in before.

Missing features stay NaN, never 0.0. NaN means 'no signal as of this date' while
0.0 is a measured neutral value. Training leaves them NaN and XGBoost handles NaN
natively, so errors='coerce' below is deliberate - do NOT add .fillna(0).
"""
from __future__ import annotations

import pandas as pd


def build_x(row, payload) -> pd.DataFrame:
    """Recreate the exact 1-row model input frame from the payload's feature_cols.

    row is a mapping (Series or dict) of raw CropFeatureDaily columns. Columns listed in
    payload['categorical'] become pandas category dtype; every other feature column is
    coerced to float64 with missing values kept as NaN.
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
