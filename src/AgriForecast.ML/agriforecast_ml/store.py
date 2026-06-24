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
    # chunksize kept small: SQL Server caps a statement at 2100 parameters
    # (~40 cols * 50 rows = 2000), so method="multi" stays under the limit.
    out.to_sql(TABLE, engine, if_exists="replace", index=False,
               chunksize=50, method="multi", dtype=_DTYPES)
    with engine.begin() as conn:
        conn.execute(text(
            f"CREATE INDEX IX_{TABLE}_Crop_Date ON {TABLE} (CropId, ObservationDate)"
        ))
    return len(out)
