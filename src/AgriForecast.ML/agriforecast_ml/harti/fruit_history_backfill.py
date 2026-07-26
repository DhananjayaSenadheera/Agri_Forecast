"""One-time backfill of extended HARTI-Pettah FRUIT history into MarketPrices, for the two
owner-adopted crops only: FRT000003 'Banana - Abul' (Ambul) and FRT000006 'Banana - Sini'
(Seeni).

The fruit-history A/B experiment showed that prepending each crop's pre-splice
HARTI-Pettah history to its national series cuts matched DEC-era MAE from 144.60 to 26.19
for Ambul and from 152.16 to 18.93 for Seeni, beating both the incumbent and the best
naive baseline. Kolikuttu was rejected (it got worse) and Papaya has no growth period, so
no harvest label. This module makes the extended series permanent for the two adopted
crops.

Mechanism: write Source='HARTI' rows into MarketPrices for PriceDate < 2025-05-05.
load.load_prices() already reads all MarketPrices rows and applies the HARTI/DEC splice,
so the extended series is served with no change to the hot read path - the same mechanism
the vegetable HARTI rows use. DEC data for these fruits starts 2025-05-05, so there is no
(CropId, PriceDate) overlap and the splice invariant holds.

The rejected alternative was unioning the pre-splice history inside load_prices() at read
time, which would put a per-crop special case and a second-table join on the hot path
forever and split the national series across two tables.

Idempotent: upsert keyed on (CropId, PriceDate, Source), so a re-run inserts 0. The
synthetic ExternalProductIds (-26, -27) continue the negative HARTI numbering and do not
collide with the existing -1..-25.

Values are built by reading each crop's Pettah PriceObservations with IsUnitConfirmed=1,
MaxPrice > 0 and ObservedDate < 2025-05-05, aggregated to one (Min, Max) per date by mean
- the same construction the experiment used. There is exactly one Pettah observation per
(crop, date), so the mean is an identity and the values round-trip exactly.

Importing this module writes nothing: backfill_fruit_history() must be called explicitly.
"""
from __future__ import annotations

import logging
import uuid
from datetime import date, datetime, timezone
from typing import Iterable

import pandas as pd
import sqlalchemy as sa

from ..db import get_engine

logger = logging.getLogger(__name__)

SOURCE = "HARTI"
PETTAH_MARKET_NAME = "Pettah (HARTI wholesale)"
SPLICE_DATE = date(2025, 5, 5)

# The two owner-adopted crops.  Ordered, code-keyed (portable across DBs).
ADOPTED_CROP_CODES: tuple[str, ...] = ("FRT000003", "FRT000006")

# Per-crop synthetic ExternalProductId for the HARTI source. Continues the negative HARTI
# numbering so these stay distinct within Source='HARTI' and satisfy the unique index
# (Source, ExternalProductId, PriceDate). Never positive - that is the DEC range.
FRUIT_EXT_PRODUCT_IDS: dict[str, int] = {
    "FRT000003": -26,  # Banana - Abul  (Ambul)
    "FRT000006": -27,  # Banana - Sini  (Seeni)
}

# Column order for the built row frame (also the parity-comparison key set).
_BUILT_COLS = [
    "CropId", "CropCode", "CropName", "PriceDate",
    "MinPrice", "MaxPrice", "AvgPrice", "ExternalProductId", "ExternalProductName",
]


def build_fruit_history_rows(
    engine: "sa.engine.Engine | None" = None,
    *,
    crop_codes: Iterable[str] = ADOPTED_CROP_CODES,
) -> pd.DataFrame:
    """Deterministically build the MarketPrices rows to backfill (no writes).

    Returns one row per (CropId, PriceDate) with MinPrice/MaxPrice = the mean of
    that date's Pettah HARTI observations (an identity in practice: one obs per
    date), AvgPrice = (Min+Max)/2, plus the synthetic ExternalProductId/Name.

    This is the SAME aggregation as the experiment's ``load_harti_pettah_presplice``
    restricted to the adopted crops, so the built values are the arm-B values.
    """
    eng = engine if engine is not None else get_engine()
    codes = list(crop_codes)
    if not codes:
        return pd.DataFrame(columns=_BUILT_COLS)

    placeholders = ", ".join(f":c{i}" for i in range(len(codes)))
    params: dict = {f"c{i}": code for i, code in enumerate(codes)}
    params["mkt"] = PETTAH_MARKET_NAME
    params["src"] = SOURCE
    params["splice"] = SPLICE_DATE.isoformat()

    sql = sa.text(
        f"""
        SELECT CONVERT(varchar(36), po.CropId) AS CropId,
               c.CropCode                       AS CropCode,
               c.Name                           AS CropName,
               po.ObservedDate                  AS PriceDate,
               po.MinPrice                      AS MinPrice,
               po.MaxPrice                      AS MaxPrice
        FROM PriceObservations po
        JOIN Crops   c ON c.Id = po.CropId
        JOIN Markets m ON m.Id = po.MarketId
        WHERE c.CropCode IN ({placeholders})
          AND m.Name = :mkt
          AND po.Source = :src
          AND po.IsUnitConfirmed = 1
          AND po.MaxPrice > 0
          AND po.ObservedDate < :splice
        """
    )
    df = pd.read_sql(sql, eng, params=params)
    if df.empty:
        return pd.DataFrame(columns=_BUILT_COLS)

    df["PriceDate"] = pd.to_datetime(df["PriceDate"])
    for col in ("MinPrice", "MaxPrice"):
        df[col] = df[col].astype(float)

    # One value per (crop, date) via mean -- identity for the single-obs corpus,
    # but explicit so the construction is robust and matches the experiment.
    df = (df.groupby(["CropId", "CropCode", "CropName", "PriceDate"],
                     as_index=False, sort=True)[["MinPrice", "MaxPrice"]].mean())
    df["AvgPrice"] = (df["MinPrice"] + df["MaxPrice"]) / 2.0
    df["ExternalProductId"] = df["CropCode"].map(FRUIT_EXT_PRODUCT_IDS).astype("int64")
    df["ExternalProductName"] = df["CropName"]

    # Hard guards: every adopted crop resolved an ext-id, and every row is
    # strictly pre-splice (so it can NEVER collide with the DEC rows).
    if df["ExternalProductId"].isna().any():
        missing = sorted(df.loc[df["ExternalProductId"].isna(), "CropCode"].unique())
        raise RuntimeError(f"no FRUIT_EXT_PRODUCT_IDS entry for: {missing}")
    if (df["PriceDate"].dt.date >= SPLICE_DATE).any():
        raise RuntimeError("built a row on/after the 2025-05-05 splice -- refusing")

    return df[_BUILT_COLS].sort_values(["CropCode", "PriceDate"]).reset_index(drop=True)


def _partition_upsert(
    rows: pd.DataFrame, existing_keys: set[tuple[str, str]]
) -> tuple[list[dict], list[dict]]:
    """Pure idempotency split: given built rows and the set of already-present
    (CropId_upper, PriceDate_iso) keys, return (to_insert, to_update) payloads.

    Kept pure so idempotency is unit-testable without touching the DB.
    """
    to_insert: list[dict] = []
    to_update: list[dict] = []
    for r in rows.to_dict("records"):
        cid = str(r["CropId"]).upper()
        pdate = pd.Timestamp(r["PriceDate"]).date()
        payload = {
            "crop_id": str(r["CropId"]),
            "price_date": pdate,
            "min_price": float(r["MinPrice"]),
            "max_price": float(r["MaxPrice"]),
            "ext_prod_id": int(r["ExternalProductId"]),
            "ext_prod_name": str(r["ExternalProductName"]),
        }
        if (cid, pdate.isoformat()) in existing_keys:
            to_update.append(payload)
        else:
            to_insert.append(payload)
    return to_insert, to_update


def backfill_fruit_history(
    engine: "sa.engine.Engine | None" = None,
    *,
    crop_codes: Iterable[str] = ADOPTED_CROP_CODES,
    dry_run: bool = False,
) -> dict:
    """Write the extended HARTI-Pettah fruit history into MarketPrices.

    Idempotent: keyed on (CropId, PriceDate, Source). Re-runs insert 0.
    EconomicCenterId is left NULL -- these are Pettah-derived rows, NOT Dambulla
    (unlike loader.upsert_harti_prices, whose legacy path IS Dambulla).

    Returns counters {built, inserted, updated, skipped_existing}.
    DO NOT call against production before the gates pass + owner merge.
    """
    eng = engine if engine is not None else get_engine()
    rows = build_fruit_history_rows(eng, crop_codes=crop_codes)
    counters = dict(built=int(len(rows)), inserted=0, updated=0, skipped_existing=0)
    if rows.empty:
        logger.warning("build_fruit_history_rows returned no rows -- nothing to backfill")
        return counters

    now_utc = datetime.now(timezone.utc)
    with eng.begin() as conn:
        # Existing (CropId, PriceDate) for Source='HARTI' among the adopted crops.
        crop_ids = sorted(rows["CropId"].str.upper().unique())
        ph = ", ".join(f":k{i}" for i in range(len(crop_ids)))
        existing = conn.execute(
            sa.text(
                f"""SELECT UPPER(CONVERT(varchar(36), CropId)) AS CropId,
                           CONVERT(varchar(10), PriceDate)     AS PriceDate
                    FROM MarketPrices
                    WHERE Source = :src
                      AND UPPER(CONVERT(varchar(36), CropId)) IN ({ph})"""
            ),
            {"src": SOURCE, **{f"k{i}": cid for i, cid in enumerate(crop_ids)}},
        ).fetchall()
        existing_keys = {(str(r[0]).upper(), str(r[1])) for r in existing}

        to_insert, to_update = _partition_upsert(rows, existing_keys)
        counters["skipped_existing"] = 0  # updates are the "already-present" set

        if dry_run:
            counters["inserted"] = len(to_insert)
            counters["updated"] = len(to_update)
            logger.info("DRY RUN -- would insert=%d update=%d (built=%d)",
                        len(to_insert), len(to_update), counters["built"])
            # roll back the (read-only) transaction implicitly on context exit
            conn.rollback()
            return counters

        for p in to_insert:
            # EconomicCenterId = NULL is deliberate: every prior HARTI row carries the Dambulla
            # centre id, but these rows come from the Pettah wholesale table, so tagging them
            # Dambulla would be false. Nothing reads the column on this path and the FK is
            # nullable, but any future per-centre analytics must LEFT-join or these rows drop.
            conn.execute(
                sa.text(
                    """INSERT INTO MarketPrices
                       (Id, CropId, EconomicCenterId, ExternalProductId,
                        ExternalProductName, PriceDate, MinPrice, MaxPrice,
                        Source, RetrievedAtUtc)
                       VALUES
                       (:id, :crop_id, NULL, :ext_prod_id, :ext_prod_name,
                        :price_date, :min_price, :max_price, :source, :retrieved_at)"""
                ),
                {"id": str(uuid.uuid4()), "crop_id": p["crop_id"],
                 "ext_prod_id": p["ext_prod_id"], "ext_prod_name": p["ext_prod_name"],
                 "price_date": p["price_date"], "min_price": p["min_price"],
                 "max_price": p["max_price"], "source": SOURCE, "retrieved_at": now_utc},
            )
            counters["inserted"] += 1

        for p in to_update:
            conn.execute(
                sa.text(
                    """UPDATE MarketPrices
                       SET MinPrice = :min_price, MaxPrice = :max_price,
                           RetrievedAtUtc = :retrieved_at
                       WHERE UPPER(CONVERT(varchar(36), CropId)) = :crop_id
                         AND PriceDate = :price_date AND Source = :source"""
                ),
                {"min_price": p["min_price"], "max_price": p["max_price"],
                 "retrieved_at": now_utc, "crop_id": p["crop_id"].upper(),
                 "price_date": p["price_date"], "source": SOURCE},
            )
            counters["updated"] += 1

    logger.info("Fruit-history backfill: built=%d inserted=%d updated=%d",
                counters["built"], counters["inserted"], counters["updated"])
    return counters
