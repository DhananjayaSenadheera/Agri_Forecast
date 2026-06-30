"""Persist the feature table to SQL Server (full idempotent rebuild)."""
from __future__ import annotations

import pandas as pd
from sqlalchemy import text
from sqlalchemy.types import NVARCHAR

from .db import get_engine

TABLE = "CropFeatureDaily"

# Sized string types so key columns are indexable (pandas defaults str -> nvarchar(max)).
_DTYPES = {
    "CropId": NVARCHAR(36),
    "CropCode": NVARCHAR(50),
    "CropName": NVARCHAR(200),
}


def write_features(df: pd.DataFrame) -> int:
    engine = get_engine()
    out = df.copy()
    out["ComputedAtUtc"] = pd.Timestamp.utcnow().tz_localize(None)
    # SQL Server caps a single parameterised statement at 2100 parameters, and
    # method="multi" sends chunksize * n_cols parameters per batch. Derive the
    # chunksize from the actual column count (budget 2000 for margin under 2100)
    # so adding feature columns can never silently breach the cap.
    chunksize = max(1, 2000 // out.shape[1])
    out.to_sql(TABLE, engine, if_exists="replace", index=False,
               chunksize=chunksize, method="multi", dtype=_DTYPES)
    with engine.begin() as conn:
        conn.execute(text(
            f"CREATE INDEX IX_{TABLE}_Crop_Date ON {TABLE} (CropId, ObservationDate)"
        ))
    return len(out)
