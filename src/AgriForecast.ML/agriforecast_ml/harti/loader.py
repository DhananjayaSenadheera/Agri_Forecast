"""HARTI price loader: CropId/MarketId resolution, the splice rule and idempotent upserts.

Splice rule (avoids double-counting in rolling features). DEC is authoritative from
2025-05-05, so HARTI rows are only inserted for PriceDate before that. Ridge Gourd and
Beans are the exception: DEC data is too noisy until 2025-06-30, so HARTI is accepted up
to and including that date for those two crops and the overlapping DEC rows are excluded
in load.load_prices() rather than deleted from the DB. The invariant is that no
(CropId, PriceDate) pair reaches the feature build from both sources.

The splice applies to upsert_harti_prices() only - the legacy Dambulla to MarketPrices
path. upsert_harti_price_observations() does not apply it: PriceObservations is a
point-in-time table whose correctness comes from AsOfUtc, not a date cutoff.

Idempotency. The MarketPrices path is keyed on (CropId, PriceDate, Source). The
PriceObservations path is name-keyed, because HARTI leaves ExternalCommodityId NULL, so
the applicable unique index is (MarketId, ExternalCommodityName, ObservedDate, Source).
Re-running either path updates existing rows in place rather than duplicating them.

Crop and market resolution. Market names resolve against the DB Markets dimension BY NAME
and never a hardcoded GUID; a name that does not resolve is WARN-skipped, never invented.
upsert_harti_price_observations() resolves CropId through canonical.CommodityAliasResolver
(a HARTI-scoped alias beats a global one). A label with no active alias is written with
CropId NULL and a WARNING - never guessed - and heals later via
canonical.heal_price_observation_crops() without re-running this loader.

Some bulletin labels are deliberately unmapped (Pumpkin, Carrot, Cabbage, Beetroot) because
the DB only has variety- or region-qualified crops and mapping would mean guessing. The
'Eggplant' row that appears from 2023-02 is HARTI relabelling the Wing Beans row, not the
DB's own Eggplant crop, so it is excluded too.

Price columns. HARTI publishes a min/max range per market per crop, so its rows always
populate MinPrice/MaxPrice and always leave WholesalePrice/RetailPrice NULL, even for
Pettah and Narahenpita. Those two columns are for sources that publish a single point
figure. The feature layer must read MinPrice/MaxPrice for Source='HARTI'.

Unit contract. HARTI daily wholesale bulletins are Rs/kg, verified corpus-wide, so every
row is written with UnitRaw and UnitConversionFactor from canonical.py and
IsUnitConfirmed=1. The prices are already LKR/kg, so no numeric conversion is applied.

Quarantine holds are STICKY-DOWN across re-ingest. IsUnitConfirmed=0, whether set by
data_quality.flag_price_outliers() or by this loader's own min>max quarantine, must survive
a re-run. This loader is designed to be re-run routinely, and a clean re-parse of a held
row always computes IsUnitConfirmed=1, so an unconditional SET would silently erase the
hold. The UPDATE branch therefore keeps an existing 0 at 0 while letting a 1 take the
incoming value, so a newly-detected min>max can still lower it. The only sanctioned way to
release a hold is data_quality.clear_outlier_hold().
"""
from __future__ import annotations

import logging
import re
import uuid
from datetime import date, datetime, time, timedelta, timezone
from typing import Sequence

import sqlalchemy as sa

from ..canonical import (
    HARTI_UNIT_CONVERSION_FACTOR,
    HARTI_UNIT_RAW,
    CommodityAliasResolver,
)
from ..data_quality import validate_price_row
from ..db import get_engine
from .parser import ParsedPrice

logger = logging.getLogger(__name__)

SOURCE = "HARTI"
# Per-label synthetic ExternalProductIds for HARTI. They MUST be distinct, because the
# unique index is (Source, ExternalProductId, PriceDate) and does not include CropId.
# Negative values avoid colliding with the DEC range of 1-101. Only crops with real
# Dambulla-column data need an entry, since this dict backs the Dambulla-only
# MarketPrices path alone.
_HARTI_PRODUCT_IDS: dict[str, int] = {
    "Beans":          -1,
    "Ladies Fingers":  -2,
    "Capsicum":       -3,
    "Bitter Gourd":   -4,
    "Luffa":          -5,
    "Snake Gourd":    -6,
    "Green Chillies": -7,
    "Tomato":         -8,
    "Leeks":          -9,
    "Knolkhol":      -10,
    "Raddish":       -11,
    # -12 is intentionally unused: it was Pumpkin, which is unmappable and never reaches here.
    "Cucumber":      -13,
    "Drumstick":     -14,
    "Long Beans":    -15,
    "Ash Plantains": -16,
    "Lime":          -17,
    "Sweet Potato":  -18,
    "Manioc":        -19,
    "Brinjals":      -20,
    "Potato (Imported)":     -21,
    "Potato (Welimada)":     -22,
    "Potato (Nuwaraeliya)":  -23,
    "Big Onion Imported":    -24,
    "Big Onion Local":       -25,
}

# Parser market name -> the exact Markets.Name string seeded by the migration. Resolution
# happens BY NAME in _build_market_map(); GUIDs are never hardcoded here. The parser's
# canonical keys, HARTI's PDF header spellings and the DB names all differ, and this dict
# is exactly the alias mapping that bridges those gaps. An unresolved DB name is
# WARN-skipped at runtime, never invented, which is the safety net if a name ever drifts.
_PARSER_MARKET_TO_DB_NAME: dict[str, str] = {
    "Dambulla":      "Dambulla Dedicated Economic Centre",
    "Pettah":        "Pettah (HARTI wholesale)",
    "Narahenpita":   "Narahenpita (HARTI retail)",
    "Thambuttegama": "Thambuttegama Dedicated Economic Centre",
    "Keppetipola":   "Keppetipola Dedicated Economic Centre",
    # Names below are byte-for-byte the seeded Market.Name values. Meegoda, Nuwara Eliya and
    # Veyangoda are Dedicated Economic Centres; Kandy, Norochchole and Bandarawela are
    # municipal wholesale markets and carry the '(HARTI wholesale)' suffix, like Pettah.
    "Kandy":         "Kandy (HARTI wholesale)",
    "Meegoda":       "Meegoda Dedicated Economic Centre",
    "Norochchole":   "Norochchole (HARTI wholesale)",
    "Nuwara Eliya":  "Nuwara Eliya Dedicated Economic Centre",
    "Bandarawela":   "Bandarawela (HARTI wholesale)",
    "Veyangoda":     "Veyangoda Dedicated Economic Centre",
}

# Splice boundary dates.
# General cutoff: HARTI rows must be before this date (exclusive).
_SPLICE_GENERAL: date = date(2025, 5, 5)

# Exception crops whose DEC data is garbage until 2025-06-30.
# For these, accept HARTI rows up to this date (inclusive).
_SPLICE_EXCEPTION_CROPS_DB_NAMES: frozenset[str] = frozenset({"Ridge Gourd", "Beans"})
_SPLICE_EXCEPTION_END: date = date(2025, 6, 30)

# HARTI label -> DB Crop.Name. This backs the legacy Dambulla-only MarketPrices path only;
# the PriceObservations path resolves crops through CommodityAliases instead. Keys are the
# canonical post-consolidation labels the parser emits ('Brinjals', not the raw pre-2023
# '(Village)'/'(Other)' cell text).
_HARTI_TO_DB_NAME: dict[str, str] = {
    "Beans":          "Beans",
    "Ladies Fingers": "Lady's Fingers",
    "Capsicum":       "Capsicum",
    "Bitter Gourd":   "Bitter Gourd",
    "Luffa":          "Ridge Gourd",
    "Snake Gourd":    "Snake Gourd",
    "Green Chillies": "Green Chili",
    "Tomato":         "Tomato",
    "Leeks":          "Leeks",
    "Knolkhol":       "Nnolkhol",           # DB's own spelling (verified live)
    "Raddish":        "Raddish",             # DB's own spelling (verified live)
    "Cucumber":       "Cucumber",
    "Drumstick":      "Drumsticks",
    "Long Beans":     "Yard - Long Beans",
    "Ash Plantains":  "Ash Plantain",
    "Lime":           "Lime",
    "Sweet Potato":   "Sweet Potato",
    "Manioc":         "Manioc",
    "Brinjals":       "Brinjal",
    "Potato (Imported)":    "Potatoes - Import",
    "Potato (Welimada)":    "Potatoes - Walimada",
    "Potato (Nuwaraeliya)": "Potatoes - Nuwaraeliya",
    "Big Onion Imported":   "Big Onion Import",
    "Big Onion Local":      "Big Onion Lanka",
    # Deliberately NOT mapped: Pumpkin, Carrot, Cabbage and Beetroot. The DB only has
    # variety- or region-qualified crops for these, so mapping the bulletin's generic row
    # would mean guessing a variety.
}


def _build_crop_map(engine: sa.engine.Engine) -> dict[str, tuple[uuid.UUID, str]]:
    """Return {harti_label: (CropId_uuid, db_crop_name)} for the target crops
    (24 label keys / 20 canonical crops as of R2 Step 6.1 -- see
    _HARTI_TO_DB_NAME)."""
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

    logger.info(
        "Crop map built: %d of %d entries resolved",
        len(result), len(_HARTI_TO_DB_NAME),
    )
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


# MarketCode of the sole Dambulla market row. Markets.MarketCode is a stable business code;
# never hardcode the row's GUID, which is per-DB.
_DAMBULLA_MARKET_CODE = "MKT00000001"


def _dambulla_market_id(engine: sa.engine.Engine) -> uuid.UUID:
    """Resolve the Dambulla Markets.Id at runtime BY CODE - never a hardcoded GUID, GUIDs are
    per-DB.

    Used to populate MarketPrices.EconomicCenterId for HARTI rows written by the legacy
    Dambulla-only path; both DAMBULLA_DEC and HARTI rows resolve to the same Dambulla market
    row. Call it once per upsert, not per row.

    Raises RuntimeError if the Dambulla market row is missing, rather than writing NULL or a
    guessed GUID.
    """
    with engine.connect() as conn:
        row = conn.execute(
            sa.text("SELECT Id FROM Markets WHERE MarketCode = :code"),
            {"code": _DAMBULLA_MARKET_CODE},
        ).fetchone()

    if row is None:
        raise RuntimeError(
            f"Dambulla market row not found (MarketCode={_DAMBULLA_MARKET_CODE!r}) "
            "-- cannot resolve EconomicCenterId for HARTI MarketPrices rows. "
            "Refusing to insert with a NULL/guessed value."
        )
    return _parse_guid(row[0])


# PDF /CreationDate format: "D:YYYYMMDDHHmmSS+HH'mm'", or a trailing Z, or no offset.
# This is HARTI's own document-creation timestamp, i.e. when the bulletin was published,
# frequently the day AFTER ObservedDate. That is exactly what AsOfUtc is for: never treat
# ObservedDate as known as of itself.
#
# The apostrophes around the offset minutes are the spec-correct form every real PDF here
# uses, but they are OPTIONAL in the regex: a delimiter-less offset would otherwise fall
# through to the 'no offset' branch and be misread as already-UTC, a silent 5.5h-early
# reading and exactly the look-ahead risk AsOfUtc exists to prevent.
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

# Conservative-late fallback publication time, in Sri Lanka local time. 06:00 SL sits
# inside the ~04:30-10:00 window real bulletins publish in, so it is late relative to the
# true publication time, not just relative to ObservedDate.
_FALLBACK_PUBLISH_TIME_SL = time(6, 0, 0)


def _resolve_as_of_utc(pdf_creation_date_raw: "str | None", observed_date: date) -> datetime:
    """Resolve the AsOfUtc (bulletin publication vintage) for a parsed row.

    Preferred source is the PDF's /CreationDate metadata, the bulletin's real publication
    timestamp. The fallback, which is rare, is ObservedDate + 1 day at 06:00 Sri Lanka time
    (00:30 UTC).

    Why conservative-late rather than end-of-day on ObservedDate: real bulletins publish the
    morning AFTER ObservedDate, so a same-day fallback would sit hours before the true
    publication window and make the observation 'known' before HARTI published it. An
    over-conservative AsOfUtc only delays when a point-in-time join sees the row; an
    under-conservative one leaks.
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
    """Upsert parsed HARTI prices into MarketPrices - Dambulla only, for back-compat.

    Keyed on (CropId, PriceDate, Source), so re-runs are safe. The parser emits rows for
    several markets, but MarketPrices has no market dimension in its unique key, so this
    legacy path filters to Dambulla; every market (including a second Dambulla copy) lands in
    PriceObservations via upsert_harti_price_observations().

    Every row gets EconomicCenterId = the Dambulla Markets.Id, resolved at runtime by code and
    cached once per call. A missing Dambulla row raises rather than writing a guess.

    Args:
        parsed_rows:  Output of parser.parse_many().
        engine:       SQLAlchemy engine; created from config if None.
        dry_run:      Resolve everything but skip DB writes.

    Returns counts: inserted, updated, skipped_splice, skipped_no_crop,
    skipped_invalid_price, skipped_non_dambulla.
    """
    if engine is None:
        engine = get_engine()

    crop_map = _build_crop_map(engine)
    dambulla_market_id = _dambulla_market_id(engine)  # cached once per run, not per row
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
                               EconomicCenterId = :economic_center_id,
                               RetrievedAtUtc = :retrieved_at
                           WHERE CONVERT(varchar(36), CropId) = :crop_id
                             AND PriceDate = :price_date
                             AND Source = :source"""
                    ),
                    {
                        "min_price": row["min_price"],
                        "max_price": row["max_price"],
                        "economic_center_id": str(dambulla_market_id),
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
                           (:id, :crop_id, :economic_center_id, :ext_prod_id,
                            :ext_prod_name, :price_date, :min_price, :max_price,
                            :source, :retrieved_at)"""
                    ),
                    {
                        "id": new_id,
                        "crop_id": crop_id_str,
                        "economic_center_id": str(dambulla_market_id),
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
    """Upsert parsed HARTI prices for ALL markets into PriceObservations.

    Additive to upsert_harti_prices(), which keeps writing Dambulla-only rows into the legacy
    MarketPrices table. This one writes every parsed market's rows, Dambulla included, so
    multi-market features have one consistent source.

    Identity and idempotency: HARTI is name-keyed, so ExternalCommodityId is always NULL and
    the applicable constraint is (MarketId, ExternalCommodityName, ObservedDate, Source).
    Matching rows are UPDATEd in place, new keys INSERTed, so it is safe to re-run.

    Point-in-time: AsOfUtc is always the bulletin's own publication timestamp from the PDF
    metadata (see _resolve_as_of_utc), never the caller's 'now'. RetrievedAtUtc is stamped now
    and is audit-only, never a feature.

    Market names resolve BY NAME once per call, never a hardcoded GUID; an unresolved name is
    WARN-skipped. Failure modes split deliberately: if the Markets or CommodityAliases lookup
    itself cannot load, this upsert ABORTS, because ingesting a whole run with everything
    unresolved is worse than failing loudly. A per-row miss only WARNs and skips, or writes
    CropId NULL, and ingestion continues.

    HARTI publishes a min/max range per market per crop, so rows always populate
    MinPrice/MaxPrice and always leave WholesalePrice/RetailPrice NULL, even for Pettah and
    Narahenpita. Those columns exist for sources that publish a single point figure.

    Args:
        parsed_rows:  Output of parser.parse_many() (multi-market).
        engine:       SQLAlchemy engine; created from config if None.
        dry_run:      Resolve everything but skip DB writes.

    Returns counts: inserted, updated, skipped_no_market, skipped_invalid_price,
    crop_resolved, crop_unresolved.
    """
    if engine is None:
        engine = get_engine()

    market_map = _build_market_map(engine)  # cached once per run, not per row
    crop_resolver = CommodityAliasResolver(engine)  # cached once per run, not per row
    now_utc = datetime.now(timezone.utc)

    counters = dict(
        inserted=0,
        updated=0,
        skipped_no_market=0,
        skipped_invalid_price=0,
        crop_resolved=0,
        crop_unresolved=0,
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

        observed_date = date.fromisoformat(pr.date_str)

        # Shared, source-agnostic row validator: a non-positive price is REJECTED (the row is not
        # written at all) and min>max is QUARANTINED (written with IsUnitConfirmed=0, never
        # silently swapped).
        validation = validate_price_row(
            min_price=pr.min_price,
            max_price=pr.max_price,
            source=SOURCE,
            crop_label=pr.harti_label,
            observed_date=observed_date,
            market_name=pr.market_name,
        )
        if validation.reject:
            logger.warning(validation.message)
            counters["skipped_invalid_price"] += 1
            continue
        if validation.hold:
            logger.warning(validation.message)

        market_id = market_map[pr.market_name]
        as_of_utc = _resolve_as_of_utc(pr.pdf_creation_date_raw, observed_date)

        # Resolve the crop through the active CommodityAliases (a HARTI-scoped alias beats a
        # global one for the same label). Never guess: an unresolved label stays NULL with a WARN
        # and the row is still inserted, because crop resolution is additive rather than a gate.
        # heal_price_observation_crops() back-fills it later as aliases are added.
        crop_id = crop_resolver.resolve(pr.harti_label, SOURCE)
        if crop_id is None:
            logger.warning(
                "[%s] %s/%s: no active CommodityAlias resolved this label — "
                "CropId left NULL, never guessed",
                pr.date_str, pr.market_name, pr.harti_label,
            )
            counters["crop_unresolved"] += 1
        else:
            counters["crop_resolved"] += 1

        to_upsert.append({
            "market_id": str(market_id),
            "crop_id": str(crop_id) if crop_id is not None else None,
            "external_commodity_name": pr.harti_label,
            "observed_date": observed_date,
            "min_price": pr.min_price,
            "max_price": pr.max_price,
            "arrivals_kg": pr.arrivals_kg,
            "as_of_utc": as_of_utc,
            "unit_raw": HARTI_UNIT_RAW,
            "unit_conversion_factor": HARTI_UNIT_CONVERSION_FACTOR,
            # HARTI's unit is a verified corpus-wide constant, so this is normally True - except that
            # a min>max hold from validate_price_row above forces it False. An ambiguous-price row
            # must not reach the feature layer regardless of unit confidence.
            "is_unit_confirmed": not validation.hold,
        })

    logger.info(
        "PriceObservations upsert candidates: %d rows (of %d parsed). "
        "no-market=%d, invalid-price=%d, crop-resolved=%d, crop-unresolved=%d",
        len(to_upsert), len(parsed_rows),
        counters["skipped_no_market"], counters["skipped_invalid_price"],
        counters["crop_resolved"], counters["crop_unresolved"],
    )

    if dry_run:
        logger.info("DRY RUN — skipping DB writes")
        counters["inserted"] = len(to_upsert)  # report as-if
        return counters

    if not to_upsert:
        return counters

    with engine.begin() as conn:
        # Look up the existing (MarketId, ExternalCommodityName, ObservedDate, Source) keys for
        # this batch, mirroring the name-keyed filtered unique index.
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
                # UPDATE the existing row. CropId uses COALESCE so a re-run can only move NULL
                # -> resolved and never overwrites an already-assigned CropId.
                #
                # IsUnitConfirmed is STICKY-DOWN. A hold (0) set by flag_price_outliers() or by
                # an earlier min>max quarantine must survive re-running this upsert with the same
                # clean parsed row, which always recomputes 1. The CASE below is a one-way
                # ratchet: already 0 stays 0, while a current 1 takes the incoming value, so a
                # genuinely ambiguous re-parse can still lower 1 -> 0. Only raising 0 -> 1 is
                # blocked, and the only way to release a hold is data_quality.clear_outlier_hold().
                conn.execute(
                    sa.text(
                        """UPDATE PriceObservations
                           SET MinPrice = :min_price,
                               MaxPrice = :max_price,
                               ArrivalsKg = :arrivals_kg,
                               AsOfUtc = :as_of_utc,
                               RetrievedAtUtc = :retrieved_at,
                               CropId = COALESCE(CropId, :crop_id),
                               UnitRaw = :unit_raw,
                               UnitConversionFactor = :unit_conversion_factor,
                               IsUnitConfirmed = CASE
                                   WHEN IsUnitConfirmed = 0 THEN 0
                                   ELSE :is_unit_confirmed
                               END
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
                        "crop_id": row["crop_id"],
                        "unit_raw": row["unit_raw"],
                        "unit_conversion_factor": row["unit_conversion_factor"],
                        "is_unit_confirmed": row["is_unit_confirmed"],
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
                            ArrivalsKg, AsOfUtc, Source, RetrievedAtUtc,
                            UnitRaw, UnitConversionFactor, IsUnitConfirmed)
                           VALUES
                           (:id, :market_id, :crop_id, NULL,
                            :ext_commodity_name, :observed_date,
                            NULL, NULL, :min_price, :max_price,
                            :arrivals_kg, :as_of_utc, :source, :retrieved_at,
                            :unit_raw, :unit_conversion_factor, :is_unit_confirmed)"""
                    ),
                    {
                        "id": new_id,
                        "market_id": row["market_id"],
                        "crop_id": row["crop_id"],
                        "ext_commodity_name": row["external_commodity_name"],
                        "observed_date": row["observed_date"],
                        "min_price": row["min_price"],
                        "max_price": row["max_price"],
                        "arrivals_kg": row["arrivals_kg"],
                        "as_of_utc": row["as_of_utc"],
                        "source": SOURCE,
                        "retrieved_at": now_utc,
                        "unit_raw": row["unit_raw"],
                        "unit_conversion_factor": row["unit_conversion_factor"],
                        "is_unit_confirmed": row["is_unit_confirmed"],
                    },
                )
                counters["inserted"] += 1

    logger.info(
        "PriceObservations DB upsert complete: inserted=%d, updated=%d",
        counters["inserted"], counters["updated"],
    )
    return counters
