"""HARTI price loader — CropId/MarketId resolution + splice rule + idempotent
DB upsert.

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

  NOTE: the splice rule is applied by upsert_harti_prices() (the legacy
  Dambulla -> MarketPrices path) ONLY. upsert_harti_price_observations()
  (the new multi-market -> PriceObservations path, R1.1 P1) does NOT apply
  the splice — PriceObservations is a fresh point-in-time table with its own
  identity and is not part of the MarketPrices/DAMBULLA_DEC splice
  invariant. Point-in-time correctness there comes from AsOfUtc, not a date
  cutoff.

IDEMPOTENCY (legacy MarketPrices path — upsert_harti_prices):
  Upsert keyed on (CropId, PriceDate, Source).  Re-running is safe — existing
  rows are updated (prices/RetrievedAtUtc refresh), not duplicated.

IDEMPOTENCY (PriceObservations path — upsert_harti_price_observations, R1.1 P1):
  HARTI is name-keyed (ExternalCommodityId is NULL), so the applicable unique
  index is UX_PriceObservations_MarketCommodityNameDateSource on
  (MarketId, ExternalCommodityName, ObservedDate, Source) — i.e. the
  idempotency key is (Source, commodity, PriceDate, MarketId), extending the
  legacy (Source, ExternalProductId, PriceDate) key with the market
  dimension per the R1.1 P1 spec.  Re-running is safe — existing rows are
  updated in place, not duplicated.

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

MARKET MAP (R1.1 P1 + R1.1 P2):
  parser.ParsedPrice.market_name values ("Dambulla", "Pettah", "Narahenpita",
  "Thambuttegama", "Keppetipola") are resolved against the DB Markets
  dimension BY NAME (never a hardcoded GUID) — see _build_market_map().
  Seeded rows (migration AddMultiMarketAndPointInTimeData): Dambulla
  Dedicated Economic Centre (MKT00000001), Keppetipola Dedicated Economic
  Centre (MKT00000002), Thambuttegama Dedicated Economic Centre
  (MKT00000003), "Pettah (HARTI wholesale)" (MKT00000004), "Narahenpita
  (HARTI retail)" (MKT00000005).  Note Keppetipola/Thambuttegama use their
  plain DEC name (same pattern as Dambulla) — unlike Pettah/Narahenpita they
  do NOT carry a "(HARTI wholesale/retail)" suffix, because (like Dambulla)
  they are seeded as their own first-class DEC market rows, not
  HARTI-only aliases of a market that also has other sources. A parsed
  market_name that does not resolve is WARN-skipped, never invented.

PRICE COLUMNS (PriceObservations):
  HARTI rows ALWAYS populate MinPrice/MaxPrice and ALWAYS leave
  WholesalePrice/RetailPrice NULL — HARTI publishes a min/max range per
  market per crop, the same convention as the legacy MarketPrices table, for
  every market including Pettah/Narahenpita. WholesalePrice/RetailPrice are
  for other sources that publish a single point figure. The feature layer
  must read MinPrice/MaxPrice for Source='HARTI', not WholesalePrice.
"""
from __future__ import annotations

import logging
import re
import uuid
from datetime import date, datetime, time, timedelta, timezone
from typing import Sequence

import sqlalchemy as sa

from ..db import get_engine
from .parser import ParsedPrice

logger = logging.getLogger(__name__)

SOURCE = "HARTI"
# Per-label synthetic ExternalProductIds for HARTI.
# MUST be distinct (the unique index is (Source, ExternalProductId, PriceDate)
# — NOT including CropId).  Negative values avoid collision with DEC range 1-101.
_HARTI_PRODUCT_IDS: dict[str, int] = {
    "Beans":         -1,
    "Ladies Fingers": -2,
    "Capsicum":      -3,
    "Bitter Gourd":  -4,
    "Luffa":         -5,
    "Snake Gourd":   -6,
}

# --------------------------------------------------------------------------
# Market name -> DB Markets.Name mapping (R1.1 P1)
# --------------------------------------------------------------------------
# parser.market_name (the canonical market key emitted by the parser) -> the
# exact Markets.Name string seeded by the AddMultiMarketAndPointInTimeData
# migration. Resolution happens BY NAME via a DB query in _build_market_map()
# — GUIDs are never hardcoded here.
#
# Thambuttegama / Keppetipola (ClickUp 86cahef44, R1.1 P2): DB names verified
# live against the Markets table (MKT00000002 / MKT00000003) — "Thambuttegama
# Dedicated Economic Centre" / "Keppetipola Dedicated Economic Centre". Note
# these DB names differ in spelling from the parser's canonical market_name
# keys and from HARTI's own PDF header text (parser key "Thambuttegama" [one
# fewer "th" than HARTI's PDF spelling "Thambuththegama"] matches the DB
# row's spelling, which was seeded independently of HARTI's bulletin text) —
# this dict is exactly the name-alias mapping that bridges that spelling gap,
# same role it already plays for Pettah->"Peliyagoda"-family HARTI headers.
_PARSER_MARKET_TO_DB_NAME: dict[str, str] = {
    "Dambulla":     "Dambulla Dedicated Economic Centre",
    "Pettah":       "Pettah (HARTI wholesale)",
    "Narahenpita":  "Narahenpita (HARTI retail)",
    "Thambuttegama": "Thambuttegama Dedicated Economic Centre",
    "Keppetipola":  "Keppetipola Dedicated Economic Centre",
}

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


def _parse_guid(raw) -> uuid.UUID:
    """Normalise a SQL Server Guid column value (str or bytes) to uuid.UUID.

    Mirrors the CropId normalisation in _build_crop_map() — pymssql may
    return a Guid as a string or as raw bytes depending on driver/column
    config, and byte-ordered Guids from SQL Server are little-endian.
    """
    if isinstance(raw, (bytes, bytearray)):
        return uuid.UUID(bytes_le=raw)
    return uuid.UUID(str(raw))


def _build_market_map(engine: sa.engine.Engine) -> dict[str, uuid.UUID]:
    """Return {parser_market_name: MarketId_uuid} resolved BY NAME against the
    DB Markets dimension — never a hardcoded GUID (R1.1 P1 risk R1).

    Cache-per-run: callers should build this once per upsert call (not per
    row) — see upsert_harti_price_observations().
    """
    db_names = list(_PARSER_MARKET_TO_DB_NAME.values())
    placeholders = ", ".join(f":n{i}" for i in range(len(db_names)))
    params = {f"n{i}": n for i, n in enumerate(db_names)}

    with engine.connect() as conn:
        rows = conn.execute(
            sa.text(f"SELECT Id, Name FROM Markets WHERE Name IN ({placeholders})"),
            params,
        ).fetchall()

    db_name_to_id: dict[str, uuid.UUID] = {}
    for row in rows:
        db_name_to_id[row[1]] = _parse_guid(row[0])

    result: dict[str, uuid.UUID] = {}
    for parser_name, db_name in _PARSER_MARKET_TO_DB_NAME.items():
        if db_name not in db_name_to_id:
            logger.warning(
                "DB market not found for parser market_name %r (expected DB name %r) "
                "— rows for this market will be skipped, not invented",
                parser_name, db_name,
            )
            continue
        result[parser_name] = db_name_to_id[db_name]

    logger.info(
        "Market map built: %d of %d entries resolved",
        len(result), len(_PARSER_MARKET_TO_DB_NAME),
    )
    return result


# --------------------------------------------------------------------------
# Bulletin publication timestamp (AsOfUtc) extraction
# --------------------------------------------------------------------------
# PDF /CreationDate format: "D:YYYYMMDDHHmmSS+HH'mm'" (or trailing Z / no
# offset).  This is HARTI's own document-creation timestamp -- i.e. when the
# bulletin was actually published, which is frequently the day AFTER the
# ObservedDate (bulletins are typically finalised the next morning).  This is
# exactly the point-in-time vintage PriceObservation.AsOfUtc is for: never
# treat ObservedDate as "known" as of itself.
#
# The apostrophe delimiters around the offset minutes ("+05'30'") are the
# PDF-spec-correct form and are what every real PDF observed in the corpus
# uses, but they are made OPTIONAL here ("+0530" also matches): a
# delimiter-less offset falling through this regex would previously hit the
# "no offset info present" branch and be misread as already-UTC -- a silent
# 5.5h-early misread that is exactly the kind of look-ahead risk AsOfUtc
# exists to prevent. Matching it correctly (rather than falling back) is
# strictly safer than the WARN-and-fall-back path.
_PDF_DATE_RE = re.compile(
    r"D:(\d{4})(\d{2})(\d{2})(\d{2})(\d{2})(\d{2})"
    r"(?:([+-])(\d{2})'?(\d{2})'?|Z)?"
)


def _parse_pdf_creation_date(raw: "str | None") -> "datetime | None":
    """Parse a PDF /CreationDate string into a tz-aware UTC datetime.

    Returns None if ``raw`` is missing or does not match the expected
    format — callers must fall back explicitly (see
    _resolve_as_of_utc), never silently default to epoch/now in a way that
    could violate the AsOfUtc-required leakage guard.
    """
    if not raw:
        return None
    m = _PDF_DATE_RE.match(raw)
    if not m:
        return None
    y, mo, d, h, mi, s, sign, tzh, tzm = m.groups()
    try:
        naive = datetime(int(y), int(mo), int(d), int(h), int(mi), int(s))
    except ValueError:
        return None
    if sign is None:
        # 'Z' or no offset info present -> already UTC
        return naive.replace(tzinfo=timezone.utc)
    offset = timedelta(hours=int(tzh), minutes=int(tzm))
    if sign == "-":
        offset = -offset
    local = naive.replace(tzinfo=timezone(offset))
    return local.astimezone(timezone.utc)


_SRI_LANKA_OFFSET = timedelta(hours=5, minutes=30)

# Conservative-late fallback publication time-of-day, in Sri Lanka local time
# (see _resolve_as_of_utc for the reasoning). 06:00 SL is safely AFTER the
# ~04:30-10:00 SL window genuine HARTI bulletins are observed to publish in
# (per real /CreationDate timestamps in the corpus) -- i.e. still
# conservative-late relative to the true publication time, not just relative
# to ObservedDate.
_FALLBACK_PUBLISH_TIME_SL = time(6, 0, 0)


def _resolve_as_of_utc(pdf_creation_date_raw: "str | None", observed_date: date) -> datetime:
    """Resolve the AsOfUtc (bulletin publication vintage) for a parsed row.

    Preferred source: the PDF's /CreationDate metadata (the bulletin's real
    publication timestamp).

    Fallback (WARN, rare — /CreationDate was found on 100% of a 40-PDF
    spot-check across the corpus): ObservedDate + 1 day at 06:00 Sri Lanka
    time (+05:30) = ObservedDate+1 00:30 UTC.

    Why conservative-LATE, not end-of-day-on-ObservedDate: genuine HARTI
    bulletins are observed (real /CreationDate timestamps across the corpus)
    to publish the morning AFTER ObservedDate, roughly 04:30-10:00 Sri Lanka
    time -- i.e. ObservedDate+1 ~23:00-04:30 UTC. A same-day 23:59:59 UTC
    fallback would sit ~4-5 hours EARLIER than that real publication window,
    which is a look-ahead leakage risk (the fallback would make the
    observation "known" before HARTI actually published it). Erring late
    instead of early is always the safe direction for AsOfUtc: an
    over-conservative AsOfUtc merely delays when a point-in-time join sees
    the row (never wrong, only late); an under-conservative one leaks.
    """
    parsed = _parse_pdf_creation_date(pdf_creation_date_raw)
    if parsed is not None:
        return parsed
    logger.warning(
        "[%s] PDF /CreationDate missing or unparsable (%r) — falling back to "
        "a conservative-late vintage (ObservedDate+1 06:00 Sri Lanka time) "
        "for AsOfUtc",
        observed_date.isoformat(), pdf_creation_date_raw,
    )
    fallback_local = datetime.combine(
        observed_date + timedelta(days=1), _FALLBACK_PUBLISH_TIME_SL,
        tzinfo=timezone(_SRI_LANKA_OFFSET),
    )
    return fallback_local.astimezone(timezone.utc)


def upsert_harti_prices(
    parsed_rows: Sequence[ParsedPrice],
    *,
    engine: sa.engine.Engine | None = None,
    dry_run: bool = False,
) -> dict:
    """Upsert parsed HARTI prices into MarketPrices — Dambulla only (back-compat).

    Keyed on (CropId, PriceDate, Source) — idempotent re-runs are safe.

    R1.1 P1 note: parser.parse_pdf() now emits rows for multiple markets
    (Dambulla, Pettah, Narahenpita).  MarketPrices has no MarketId concept
    (it always implicitly meant Dambulla) and its unique key
    (Source, ExternalProductId, PriceDate) has no market dimension to
    extend — so this legacy path deliberately filters to Dambulla-only rows
    to preserve exact back-compat behaviour.  Multi-market rows (including a
    second Dambulla copy) land in PriceObservations instead — see
    upsert_harti_price_observations().

    Args:
        parsed_rows:  Output of parser.parse_many().
        engine:       SQLAlchemy engine; created from config if None.
        dry_run:      If True, resolve everything but skip DB writes (for testing).

    Returns:
        Dict with counts: inserted, updated, skipped_splice, skipped_no_crop,
        skipped_invalid_price, skipped_non_dambulla.
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
        skipped_non_dambulla=0,
    )

    # Build the upsert payload (only valid, splice-allowed, Dambulla rows)
    to_upsert: list[dict] = []

    for pr in parsed_rows:
        # Back-compat filter: MarketPrices is Dambulla-only (R1.1 P1).
        if pr.market_name != "Dambulla":
            counters["skipped_non_dambulla"] += 1
            continue

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
            "ext_prod_id": _HARTI_PRODUCT_IDS[pr.harti_label],
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
                        "ext_prod_id": row["ext_prod_id"],
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


def upsert_harti_price_observations(
    parsed_rows: Sequence[ParsedPrice],
    *,
    engine: sa.engine.Engine | None = None,
    dry_run: bool = False,
) -> dict:
    """Upsert parsed HARTI prices (ALL markets: Dambulla, Pettah, Narahenpita)
    into PriceObservations — the R1.1 P1 multi-market, point-in-time table.

    This is additive to upsert_harti_prices(): that function keeps writing
    Dambulla-only rows into the legacy MarketPrices table for back-compat;
    this function writes every parsed market's rows (Dambulla included) into
    PriceObservations so downstream multi-market features have a single
    consistent source.

    Identity / idempotency (R1.1 P1 spec: (Source, commodity, PriceDate,
    MarketId)):
      HARTI is name-keyed (no ExternalCommodityId), so ExternalCommodityId is
      always NULL and the applicable DB constraint is
      UX_PriceObservations_MarketCommodityNameDateSource on
      (MarketId, ExternalCommodityName, ObservedDate, Source) — i.e. exactly
      (Source, commodity, PriceDate, MarketId) with "commodity" resolved via
      name.  Existing rows are UPDATEd in place on a key match; new keys are
      INSERTed.  Safe to re-run.

    Point-in-time contract:
      AsOfUtc is ALWAYS the bulletin's own publication timestamp (PDF
      /CreationDate metadata, resolved via _resolve_as_of_utc — never
      caller/system "now").  RetrievedAtUtc is stamped as "now" here (audit
      only, never a feature) — mirrors NewsArticles/CropFeatureDaily write
      patterns elsewhere in this package.

    Market resolution (risk R1): parser market_name -> DB Markets.Id is
    resolved BY NAME once per call (_build_market_map(), cached for the
    duration of this call) — never a hardcoded GUID.  A market_name that
    does not resolve is WARN-skipped, never invented.

    Price column convention: HARTI publishes a single Min/Max wholesale
    range per market per crop (no separate wholesale-vs-retail split, no
    single-point price) — the same convention the legacy MarketPrices table
    uses. HARTI rows therefore ALWAYS populate MinPrice/MaxPrice and ALWAYS
    leave WholesalePrice/RetailPrice NULL, even for Pettah/Narahenpita
    (their bulletin cells are min/max ranges too, not a single figure).
    WholesalePrice/RetailPrice exist on PriceObservations for OTHER sources
    (e.g. CBSL) that publish a single point figure per series — the future
    feature layer must read MinPrice/MaxPrice (or their midpoint) for
    Source='HARTI' rows, not WholesalePrice, which will be NULL here.

    Args:
        parsed_rows:  Output of parser.parse_many() (multi-market).
        engine:       SQLAlchemy engine; created from config if None.
        dry_run:      If True, resolve everything but skip DB writes (for testing).

    Returns:
        Dict with counts: inserted, updated, skipped_no_market,
        skipped_invalid_price.
    """
    if engine is None:
        engine = get_engine()

    market_map = _build_market_map(engine)  # cached once per run, not per row
    now_utc = datetime.now(timezone.utc)

    counters = dict(
        inserted=0,
        updated=0,
        skipped_no_market=0,
        skipped_invalid_price=0,
    )

    to_upsert: list[dict] = []

    for pr in parsed_rows:
        if pr.market_name not in market_map:
            logger.warning(
                "[%s] %s: market_name %r did not resolve to a DB Market — "
                "skipping row, not inventing a market",
                pr.date_str, pr.harti_label, pr.market_name,
            )
            counters["skipped_no_market"] += 1
            continue

        if pr.min_price <= 0 or pr.max_price <= 0:
            logger.debug(
                "[%s] %s/%s: invalid price (min=%.2f max=%.2f) — skipping",
                pr.date_str, pr.market_name, pr.harti_label, pr.min_price, pr.max_price,
            )
            counters["skipped_invalid_price"] += 1
            continue

        market_id = market_map[pr.market_name]
        observed_date = date.fromisoformat(pr.date_str)
        as_of_utc = _resolve_as_of_utc(pr.pdf_creation_date_raw, observed_date)

        to_upsert.append({
            "market_id": str(market_id),
            "external_commodity_name": pr.harti_label,
            "observed_date": observed_date,
            "min_price": pr.min_price,
            "max_price": pr.max_price,
            "arrivals_kg": pr.arrivals_kg,
            "as_of_utc": as_of_utc,
        })

    logger.info(
        "PriceObservations upsert candidates: %d rows (of %d parsed). "
        "no-market=%d, invalid-price=%d",
        len(to_upsert), len(parsed_rows),
        counters["skipped_no_market"], counters["skipped_invalid_price"],
    )

    if dry_run:
        logger.info("DRY RUN — skipping DB writes")
        counters["inserted"] = len(to_upsert)  # report as-if
        return counters

    if not to_upsert:
        return counters

    with engine.begin() as conn:
        # Look up existing (MarketId, ExternalCommodityName, ObservedDate, Source)
        # keys for the candidate batch (mirrors the name-keyed filtered unique
        # index UX_PriceObservations_MarketCommodityNameDateSource).
        existing_keys: set[tuple[str, str, str]] = set()
        unique_market_ids = list({r["market_id"] for r in to_upsert})
        chunk_size = 200

        if unique_market_ids:
            placeholders = ", ".join(f":mid{i}" for i in range(len(unique_market_ids)))
            params_mids = {f"mid{i}": mid for i, mid in enumerate(unique_market_ids)}
            existing_rows = conn.execute(
                sa.text(
                    f"""SELECT CONVERT(varchar(36), MarketId) as MarketId,
                               ExternalCommodityName,
                               CONVERT(varchar(10), ObservedDate) as ObservedDate
                        FROM PriceObservations
                        WHERE Source = :src
                          AND ExternalCommodityId IS NULL
                          AND MarketId IN ({placeholders})"""
                ),
                {"src": SOURCE, **params_mids},
            ).fetchall()
            existing_keys = {
                (str(r[0]).upper(), str(r[1]), str(r[2])) for r in existing_rows
            }

        for row in to_upsert:
            key = (
                row["market_id"].upper(),
                row["external_commodity_name"],
                row["observed_date"].isoformat(),
            )
            if key in existing_keys:
                # UPDATE existing row
                conn.execute(
                    sa.text(
                        """UPDATE PriceObservations
                           SET MinPrice = :min_price,
                               MaxPrice = :max_price,
                               ArrivalsKg = :arrivals_kg,
                               AsOfUtc = :as_of_utc,
                               RetrievedAtUtc = :retrieved_at
                           WHERE CONVERT(varchar(36), MarketId) = :market_id
                             AND ExternalCommodityName = :ext_commodity_name
                             AND ExternalCommodityId IS NULL
                             AND ObservedDate = :observed_date
                             AND Source = :source"""
                    ),
                    {
                        "min_price": row["min_price"],
                        "max_price": row["max_price"],
                        "arrivals_kg": row["arrivals_kg"],
                        "as_of_utc": row["as_of_utc"],
                        "retrieved_at": now_utc,
                        "market_id": row["market_id"],
                        "ext_commodity_name": row["external_commodity_name"],
                        "observed_date": row["observed_date"],
                        "source": SOURCE,
                    },
                )
                counters["updated"] += 1
            else:
                # INSERT new row
                new_id = str(uuid.uuid4())
                conn.execute(
                    sa.text(
                        """INSERT INTO PriceObservations
                           (Id, MarketId, CropId, ExternalCommodityId,
                            ExternalCommodityName, ObservedDate,
                            WholesalePrice, RetailPrice, MinPrice, MaxPrice,
                            ArrivalsKg, AsOfUtc, Source, RetrievedAtUtc)
                           VALUES
                           (:id, :market_id, NULL, NULL,
                            :ext_commodity_name, :observed_date,
                            NULL, NULL, :min_price, :max_price,
                            :arrivals_kg, :as_of_utc, :source, :retrieved_at)"""
                    ),
                    {
                        "id": new_id,
                        "market_id": row["market_id"],
                        "ext_commodity_name": row["external_commodity_name"],
                        "observed_date": row["observed_date"],
                        "min_price": row["min_price"],
                        "max_price": row["max_price"],
                        "arrivals_kg": row["arrivals_kg"],
                        "as_of_utc": row["as_of_utc"],
                        "source": SOURCE,
                        "retrieved_at": now_utc,
                    },
                )
                counters["inserted"] += 1

    logger.info(
        "PriceObservations DB upsert complete: inserted=%d, updated=%d",
        counters["inserted"], counters["updated"],
    )
    return counters
