"""Feature-extraction contract for Model A.

Defines exactly which CropFeatureDaily columns are model inputs, loads the
labelled training rows, and produces a stable contract hash so the serving
side can verify it is feeding the model the same feature set it trained on.
"""
from __future__ import annotations

import hashlib
import pandas as pd

from ..db import get_engine

TARGET_COL = "LabelHarvestPrice"
CATEGORICAL_COLS = ["CropId"]  # crop identity for the pooled model

# Excluded: keys, label/leakage cols, bookkeeping, and raw Year (absolute year
# would not generalise with only 2 calendar years of data).
_EXCLUDE = {
    "CropCode", "CropName", "ObservationDate", "ComputedAtUtc",
    "HarvestDate", "LabelHarvestPrice", "LabelAvailable", "Year",
}


def load_training_frame() -> pd.DataFrame:
    sql = "SELECT * FROM CropFeatureDaily WHERE LabelAvailable = 1"
    df = pd.read_sql(sql, get_engine())
    df["ObservationDate"] = pd.to_datetime(df["ObservationDate"])
    df["HarvestDate"] = pd.to_datetime(df["HarvestDate"])
    return df.sort_values("ObservationDate").reset_index(drop=True)


def feature_columns(df: pd.DataFrame) -> list[str]:
    cols = [c for c in df.columns if c not in _EXCLUDE]
    # ensure categorical keys come last, numeric first (stable ordering)
    numeric = [c for c in cols if c not in CATEGORICAL_COLS]
    return sorted(numeric) + CATEGORICAL_COLS


def build_xy(df: pd.DataFrame):
    cols = feature_columns(df)
    X = df[cols].copy()
    # SQL decimals arrive as Python Decimal (object) and all-null cols as object;
    # coerce every non-categorical feature to float, keep crop identity categorical.
    for c in cols:
        if c in CATEGORICAL_COLS:
            X[c] = X[c].astype("category")
        else:
            X[c] = pd.to_numeric(X[c], errors="coerce").astype("float64")
    y = pd.to_numeric(df[TARGET_COL], errors="coerce").astype("float64")
    return X, y, cols


def contract_hash(feature_cols: list[str]) -> str:
    joined = ",".join(feature_cols)
    return hashlib.sha256(joined.encode()).hexdigest()[:16]
