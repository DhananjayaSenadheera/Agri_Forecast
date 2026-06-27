"""HARTI price loader — CropId resolution + splice rule + idempotent DB upsert.

SPLICE / DEDUP RULE (critical — avoids double-counting in rolling features):
  DEC is authoritative from 2025-05-05 onward.

  General rule:
    Insert HARTI rows ONLY for PriceDate < 2025-05-05 (pre-DEC historical tail).

  EXCEPTION — DEC launch noise (Ridge Gourd + Beans only):
    DEC data 2025-05-05 → 2025-06-30 is garbage (CV 49–58%) for these two crops.
    For Ridge Gourd and Beans: insert HARTI up to 2025-06-30 (inclusive).
    The corresponding DEC rows for those crops in that window are excluded in the
    ML load path (load_prices() in load.py) — NOT deleted from the DB.

  Net invariant: no (CropId, PriceDate) pair has both a HARTI and a DEC row
  reaching the feature build.

IDEMPOTENCY:
  Upsert keyed on (CropId, PriceDate, Source).  Re-running is safe — existing
  rows are updated (prices/RetrievedAtUtc refresh), not duplicated.

CROP MAP:
  Only 6 crops are ingested.  4 uncovered crops (Winged Bean, Ginger, Cooking
  Melon, Watermelon) have no usable HARTI Dambulla data — skip them.

  HARTI label            → DB crop Name
  "Beans"               → "Beans"
  "Ladies Fingers"      → "Lady's Fingers"
  "Capsicum"            → "Capsicum"
  "Bitter Gourd"        → "Bitter Gourd"   (includes pre-split "Bitter Gourd (Other)")
  "Luffa"               → "Ridge Gourd"
  "Snake Gourd"         → "Snake Gourd"
"""
from __future__ import annotations

import logging
import uuid
from datetime import date, datetime, timezone
from typing import Sequence

import sqlalchemy as sa

from ..db import get_engine
from .parser import ParsedPrice

logger = logging.getLogger(__name__)

SOURCE = "HARTI"
EXTERNAL_PRODUCT_ID = 0  # HARTI has no numeric product IDs

# --------------------------------------------------------------------------
# Splice boundary dates
# --------------------------------------------------------------------------
# General cutoff: HARTI rows must be before this date (exclusive).
_SPLICE_GENERAL: date = date(2025, 5, 5)

# Exception crops whose DEC data is garbage until 2025-06-30.
# For these, accept HARTI rows up to this date (inclusive).
_SPLICE_EXCEPTION_CROPS_DB_NAMES: frozenset[str] = frozenset({"Ridge Gourd", "Beans"})
_SPLICE_EXCEPTION_END: date = date(2025, 6, 30)

# --------------------------------------------------------------------------
# HARTI label → DB Crop.Name mapping
# --------------------------------------------------------------------------
_HARTI_TO_DB_NAME: dict[str, str] = {
    "Beans":         "Beans",
    "Ladies Fingers":"Lady's Fingers",
    "Capsicum":      "Capsicum",
    "Bitter Gourd":  "Bitter Gourd",
    "Luffa":         "Ridge Gourd",
    "Snake Gourd":   "Snake Gourd",
}


def _build_crop_map(engine: sa.engine.Engine) -> dict[str, tuple[uuid.UUID, str]]:
    """Return {harti_label: (CropId_uuid, db_crop_name)} for the 6 target crops."""
    db_names = list(_HARTI_TO_DB_NAME.values())
    placeholders = ", ".join(f":n{i}" for i in range(len(db_names)))
    params = {f"n{i}": n for i, n in enumerate(db_names)}

    with engine.connect() as conn:
        rows = conn.execute(
            sa.text(f"SELECT Id, Name FROM Crops WHERE Name IN ({placeholders})"),
            params,
        ).fetchall()

    db_name_to_id: dict[str, uuid.UUID] = {}
    for row in rows:
        raw_id = row[0]
        # SQL Server returns Guid as a string or bytes; normalise
        if isinstance(raw_id, (bytes, bytearray)):
            crop_id = uuid.UUID(bytes_le=raw_id)
        else:
            crop_id = uuid.UUID(str(raw_id))
        db_name_to_id[row[1]] = crop_id

    result: dict[str, tuple[uuid.UUID, str]] = {}
    for harti_label, db_name in _HARTI_TO_DB_NAME.items():
        if db_name not in db_name_to_id:
            logger.warning(
                "DB crop not found for HARTI label %r (expected DB name %r) — skipping",
                harti_label, db_name,
            )
            continue
        result[harti_label] = (db_name_to_id[db_name], db_name)

    logger.info("Crop map built: %d of 6 entries resolved", len(result))
    return result


def _splice_allowed(price_date: date, db_name: str) -> bool:
    """True if this (date, crop) should be inserted per the splice rule."""
    if db_name in _SPLICE_EXCEPTION_CROPS_DB_NAMES:
        # Exception crops: accept up to and including 2025-06-30
        return price_date <= _SPLICE_EXCEPTION_END
    # General: only pre-DEC historical tail
    return price_date < _SPLICE_GENERAL


def upsert_harti_prices(
    parsed_rows: Sequence[ParsedPrice],
    *,
    engine: sa.engine.Engine | None = None,
    dry_run: bool = False,
) -> dict:
    """Upsert parsed HARTI prices into MarketPrices.

    Keyed on (CropId, PriceDate, Source) — idempotent re-runs are safe.

    Args:
        parsed_rows:  Output of parser.parse_many().
        engine:       SQLAlchemy engine; created from config if None.
        dry_run:      If True, resolve everything but skip DB writes (for testing).

    Returns:
        Dict with counts: inserted, updated, skipped_splice, skipped_no_crop,
        skipped_invalid_price.
    """
    if engine is None:
        engine = get_engine()

    crop_map = _build_crop_map(engine)
    now_utc = datetime.now(timezone.utc)

    counters = dict(
        inserted=0,
        updated=0,
        skipped_splice=0,
        skipped_no_crop=0,
        skipped_invalid_price=0,
    )

    # Build the upsert payload (only valid, splice-allowed rows)
    to_upsert: list[dict] = []

    for pr in parsed_rows:
        # Validate prices
        if pr.min_price <= 0 or pr.max_price <= 0:
            logger.debug(
                "[%s] %s: invalid price (min=%.2f max=%.2f) — skipping",
                pr.date_str, pr.harti_label, pr.min_price, pr.max_price,
            )
            counters["skipped_invalid_price"] += 1
            continue

        if pr.harti_label not in crop_map:
            counters["skipped_no_crop"] += 1
            continue

        crop_id, db_name = crop_map[pr.harti_label]
        price_date = date.fromisoformat(pr.date_str)

        if not _splice_allowed(price_date, db_name):
            counters["skipped_splice"] += 1
            continue

        to_upsert.append({
            "crop_id": str(crop_id),
            "db_name": db_name,
            "harti_label": pr.harti_label,
            "price_date": price_date,
            "min_price": pr.min_price,
            "max_price": pr.max_price,
        })

    logger.info(
        "Upsert candidates: %d rows (of %d parsed). Splice-skipped=%d, "
        "no-crop=%d, invalid-price=%d",
        len(to_upsert), len(parsed_rows),
        counters["skipped_splice"], counters["skipped_no_crop"],
        counters["skipped_invalid_price"],
    )

    if dry_run:
        logger.info("DRY RUN — skipping DB writes")
        counters["inserted"] = len(to_upsert)  # report as-if
        return counters

    if not to_upsert:
        return counters

    with engine.begin() as conn:
        # Check which (CropId, PriceDate, Source) already exist
        # Build set of existing keys for efficient lookup
        existing_keys: set[tuple[str, str]] = set()
        # Query in chunks to avoid massive IN clauses
        chunk_size = 500
        all_pairs = [(r["crop_id"], r["price_date"].isoformat()) for r in to_upsert]
        unique_crop_ids = list({r["crop_id"] for r in to_upsert})

        if unique_crop_ids:
            placeholders = ", ".join(f":cid{i}" for i in range(len(unique_crop_ids)))
            params_cids = {f"cid{i}": cid for i, cid in enumerate(unique_crop_ids)}
            existing_rows = conn.execute(
                sa.text(
                    f"""SELECT CONVERT(varchar(36), CropId) as CropId,
                               CONVERT(varchar(10), PriceDate) as PriceDate
                        FROM MarketPrices
                        WHERE Source = :src
                          AND CropId IN ({placeholders})"""
                ),
                {"src": SOURCE, **params_cids},
            ).fetchall()
            existing_keys = {(str(r[0]).upper(), str(r[1])) for r in existing_rows}

        for row in to_upsert:
            key = (row["crop_id"].upper(), row["price_date"].isoformat())
            crop_id_str = row["crop_id"]
            if key in existing_keys:
                # UPDATE existing row
                conn.execute(
                    sa.text(
                        """UPDATE MarketPrices
                           SET MinPrice = :min_price,
                               MaxPrice = :max_price,
                               RetrievedAtUtc = :retrieved_at
                           WHERE CONVERT(varchar(36), CropId) = :crop_id
                             AND PriceDate = :price_date
                             AND Source = :source"""
                    ),
                    {
                        "min_price": row["min_price"],
                        "max_price": row["max_price"],
                        "retrieved_at": now_utc,
                        "crop_id": crop_id_str,
                        "price_date": row["price_date"],
                        "source": SOURCE,
                    },
                )
                counters["updated"] += 1
            else:
                # INSERT new row
                new_id = str(uuid.uuid4())
                conn.execute(
                    sa.text(
                        """INSERT INTO MarketPrices
                           (Id, CropId, EconomicCenterId, ExternalProductId,
                            ExternalProductName, PriceDate, MinPrice, MaxPrice,
                            Source, RetrievedAtUtc)
                           VALUES
                           (:id, :crop_id, NULL, :ext_prod_id,
                            :ext_prod_name, :price_date, :min_price, :max_price,
                            :source, :retrieved_at)"""
                    ),
                    {
                        "id": new_id,
                        "crop_id": crop_id_str,
                        "ext_prod_id": EXTERNAL_PRODUCT_ID,
                        "ext_prod_name": row["harti_label"],
                        "price_date": row["price_date"],
                        "min_price": row["min_price"],
                        "max_price": row["max_price"],
                        "source": SOURCE,
                        "retrieved_at": now_utc,
                    },
                )
                counters["inserted"] += 1

    logger.info(
        "DB upsert complete: inserted=%d, updated=%d",
        counters["inserted"], counters["updated"],
    )
    return counters
