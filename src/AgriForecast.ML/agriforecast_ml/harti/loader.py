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
  R2 Step 6.1 (D-DF6, 2026-07-07) widened this from 6 to 20 mappable
  canonical crops (24 exact label keys incl. potato/onion variants)
  -- see _HARTI_TO_DB_NAME for the full current mapping and the excluded/
  unmappable labels list (Pumpkin, Carrot, Cabbage, Beetroot: DB only has
  region/variety-qualified names, no plain crop, so mapping would mean
  guessing a variety -- deliberately not done). 4 ORIGINALLY-uncovered
  crops (Winged Bean, Ginger, Cooking Melon, Watermelon) still have no
  usable HARTI Dambulla data under their own DB names — skip them. (NOTE:
  from 2023-02-01 the bulletin carries a row literally labelled "Eggplant"
  at the position formerly held by "Dambala (Wing Beans)" — this is HARTI's
  own relabelling of the Wing Beans row, not DB's distinct "Eggplant" crop,
  and is deliberately excluded from _TARGET_CROPS/_HARTI_TO_DB_NAME to avoid
  a false alias merge; see parser.py's _TARGET_CROPS docstring.)

MARKET MAP (R1.1 P1 + R1.1 P2 + R2 Step 6.1):
  parser.ParsedPrice.market_name values ("Dambulla", "Pettah", "Narahenpita",
  "Thambuttegama", "Keppetipola", "Kandy", "Meegoda", "Norochchole",
  "Nuwara Eliya", "Bandarawela", "Veyangoda") are resolved against the DB
  Markets dimension BY NAME (never a hardcoded GUID) — see
  _build_market_map().  Seeded rows (migration
  AddMultiMarketAndPointInTimeData): Dambulla Dedicated Economic Centre
  (MKT00000001), Keppetipola Dedicated Economic Centre (MKT00000002),
  Thambuttegama Dedicated Economic Centre (MKT00000003), "Pettah (HARTI
  wholesale)" (MKT00000004), "Narahenpita (HARTI retail)" (MKT00000005).
  Note Keppetipola/Thambuttegama use their plain DEC name (same pattern as
  Dambulla) — unlike Pettah/Narahenpita they do NOT carry a "(HARTI
  wholesale/retail)" suffix, because (like Dambulla) they are seeded as
  their own first-class DEC market rows, not HARTI-only aliases of a
  market that also has other sources. A parsed market_name that does not
  resolve is WARN-skipped, never invented.

  The 6 R2 Step 6.1 markets (Kandy, Meegoda, Norochchole, Nuwara Eliya,
  Bandarawela, Veyangoda) have NO Markets row yet (live-queried, confirmed
  absent — subtask 6.2's job) — _PARSER_MARKET_TO_DB_NAME carries
  PLACEHOLDER "(HARTI wholesale)"-suffixed names for them today, which
  legitimately WARN-skip every row until 6.2 creates the matching rows.

PRICE COLUMNS (PriceObservations):
  HARTI rows ALWAYS populate MinPrice/MaxPrice and ALWAYS leave
  WholesalePrice/RetailPrice NULL — HARTI publishes a min/max range per
  market per crop, the same convention as the legacy MarketPrices table, for
  every market including Pettah/Narahenpita. WholesalePrice/RetailPrice are
  for other sources that publish a single point figure. The feature layer
  must read MinPrice/MaxPrice for Source='HARTI', not WholesalePrice.

CROP RESOLUTION (R1.1 P1 Stage B, canonical mapping layer):
  upsert_harti_price_observations() resolves each row's CropId via
  ..canonical.CommodityAliasResolver, loaded once per call (cache-per-run,
  same pattern as _build_market_map()) against active CommodityAliases rows
  scoped Source='HARTI' (falling back to a global alias if no HARTI-scoped
  one matches). A label with no active alias match is written with
  CropId = NULL and logged as a WARNING — never guessed, never
  fuzzy-matched. Existing NULL-CropId rows self-heal later via
  canonical.heal_price_observation_crops() as new aliases are added; this
  loader does not need to be re-run for that.

UNIT CONTRACT (R1.1 P1 Stage B):
  HARTI daily wholesale bulletins are Rs/kg, verified corpus-wide (bulletin
  header states "... (Rs./kg)" for every located market column in the same
  table — see harti_multimarket_audit.md). Every row this loader writes
  therefore sets UnitRaw=canonical.HARTI_UNIT_RAW ("Rs/kg"),
  UnitConversionFactor=canonical.HARTI_UNIT_CONVERSION_FACTOR (1.0), and
  IsUnitConfirmed=1 — per the fail-closed contract documented in
  canonical.py, a source writer may only set IsUnitConfirmed=1 when the unit
  is a verified constant of that source (true here) or a per-row confirmed
  conversion. MinPrice/MaxPrice are already the LKR/kg figures HARTI
  publishes, so no numeric conversion is applied (factor 1.0).

QUARANTINE HOLDS ARE STICKY-DOWN ACROSS RE-INGEST (R1.1 P1 Step 5,
ClickUp 86cahef64): IsUnitConfirmed=0 set by data_quality.flag_price_outliers()
or by this loader's own min>max quarantine (see data_quality.validate_price_row)
is a ONE-WAY gate with respect to routine re-ingestion. upsert_harti_price_
observations() is designed to be re-run repeatedly (ingest_harti.py
--no-download re-parses the whole cache every time it runs) and is
idempotent on every OTHER column — but IsUnitConfirmed must NOT be
"idempotent" in the naive sense of "just re-write whatever the fresh parse
says," because a fresh, clean re-parse of an already-held row always
computes IsUnitConfirmed=1 (validation.hold=False for a normal min<=max
row), which would silently erase the hold the very next time this loader
runs — defeating flag_price_outliers()'s entire quarantine mechanism. The
UPDATE branch therefore uses
  IsUnitConfirmed = CASE WHEN IsUnitConfirmed = 0 THEN 0 ELSE :is_unit_confirmed END
i.e. already-0 (held) stays 0 regardless of what this run's row computes;
already-1 (not held) takes the incoming value normally, so a newly-detected
min>max on a previously-clean row can still transition 1 -> 0 in the same
write. The ONLY sanctioned way to release an existing hold is
data_quality.clear_outlier_hold() (explicit, single-row, parameterized,
never a bulk/incidental side effect of re-ingesting).
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
# Per-label synthetic ExternalProductIds for HARTI.
# MUST be distinct (the unique index is (Source, ExternalProductId, PriceDate)
# — NOT including CropId).  Negative values avoid collision with DEC range 1-101.
#
# R2 Step 6.1 (D-DF6) note: this dict backs ONLY upsert_harti_prices() (the
# legacy Dambulla-only MarketPrices path), which filters to market_name ==
# "Dambulla" before ever looking up an ExternalProductId -- so only crops
# with real Dambulla-column data need an entry here. New IDs continue the
# existing negative-integer numbering (-7, -8, ...), never colliding with
# the original -1..-6 or the DEC positive range (1-101).
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
    # -12 intentionally unused (was Pumpkin -- removed, see parser.py
    # _TARGET_CROPS docstring: Pumpkin is unmappable, never reaches this dict)
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
#
# R2 Step 6.2 (D-DF6): the 6 additional markets (Kandy, Meegoda,
# Norochchole, Nuwara Eliya, Bandarawela, Veyangoda) now have live Markets
# rows (MKT00000007..MKT00000012, seeded by SeedMarkets in
# AgriForecastDbContext.cs).  These Name strings are byte-for-byte the
# seeded Market.Name values.  Classification was owner web-verified in 6.2
# and split: Meegoda / Nuwara Eliya / Veyangoda are formally-designated
# Dedicated Economic Centres; Kandy / Norochchole / Bandarawela are
# municipal/assembly wholesale markets ("(HARTI wholesale)" suffix, like
# Pettah).  Per the fail-closed contract in _build_market_map() below, an
# unresolved DB name is WARN-skipped at runtime, never invented -- kept as
# a live safety net if a name ever drifts.
_PARSER_MARKET_TO_DB_NAME: dict[str, str] = {
    "Dambulla":      "Dambulla Dedicated Economic Centre",
    "Pettah":        "Pettah (HARTI wholesale)",
    "Narahenpita":   "Narahenpita (HARTI retail)",
    "Thambuttegama": "Thambuttegama Dedicated Economic Centre",
    "Keppetipola":   "Keppetipola Dedicated Economic Centre",
    # -- R2 Step 6.2: live Markets rows now exist (MKT00000007..MKT00000012).
    #    Names are byte-for-byte the seeded Market.Name values (SeedMarkets in
    #    AgriForecastDbContext.cs). Classification per owner web-verification:
    #    Meegoda / Nuwara Eliya / Veyangoda = Dedicated Economic Centres;
    #    Kandy / Norochchole / Bandarawela = "(HARTI wholesale)" markets. --
    "Kandy":         "Kandy (HARTI wholesale)",
    "Meegoda":       "Meegoda Dedicated Economic Centre",
    "Norochchole":   "Norochchole (HARTI wholesale)",
    "Nuwara Eliya":  "Nuwara Eliya Dedicated Economic Centre",
    "Bandarawela":   "Bandarawela (HARTI wholesale)",
    "Veyangoda":     "Veyangoda Dedicated Economic Centre",
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
# R2 Step 6.1 (D-DF6) note: this dict backs upsert_harti_prices() (the
# legacy Dambulla-only MarketPrices path) ONLY -- it is NOT the
# CommodityAliases fail-closed resolver that upsert_harti_price_
# observations() uses for the PriceObservations multi-market path (see
# CommodityAliasResolver in canonical.py, extended separately in subtask
# 6.2). Keys are the CANONICAL harti_label values emitted by parser.
# _TARGET_CROPS (i.e. the post-consolidation label -- "Brinjals", not the
# raw pre-2023 "Brinjals (Village)"/"Brinjals (Other)" PDF cell text).
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
    # NOT mapped (deliberately excluded from _TARGET_CROPS -- see parser.py
    # docstring and the Step 6.1 report "unmappable labels" for the full
    # reasoning): Pumpkin (DB only has "Pumpkin - Big"/"Pumpkin - Malashian",
    # no plain "Pumpkin" -- picking one would be guessing); Carrot, Cabbage,
    # Beetroot (DB only has region-qualified variants, no plain crop; the
    # bulletin's own region-qualified rows -- e.g. "Cabbage (Kandy)" --
    # still don't line up 1:1 with DB's region choices without guessing).
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


# MarketCode of the sole Dambulla market row (Markets.MarketCode is a stable
# business code, seeded by migration -- NEVER hardcode the row's GUID, which
# is per-DB; see D-DF3 / R2 Step 3 in the project memory).
_DAMBULLA_MARKET_CODE = "MKT00000001"


def _dambulla_market_id(engine: sa.engine.Engine) -> uuid.UUID:
    """Resolve the Dambulla Markets.Id at runtime (BY CODE, never a hardcoded
    GUID -- GUIDs are per-DB).

    Used to populate MarketPrices.EconomicCenterId for HARTI rows written by
    upsert_harti_prices() (the legacy Dambulla-only MarketPrices path): the
    R2 Step-3 Markets/EconomicCenters merge repoints EconomicCenterId at
    Markets.Id, and both DAMBULLA_DEC and HARTI rows resolve to the same
    Dambulla market row (hub-adjudicated -- the legacy HARTI splice loader
    IS Dambulla prices).

    Cache-per-run: callers should call this once per upsert call (mirrors
    _build_crop_map() / _build_market_map()), not per row.

    Raises RuntimeError if the Dambulla market row is missing -- fails
    loudly rather than silently writing NULL/a guessed GUID.
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

    EconomicCenterId (R2 Step 3, Markets/EconomicCenters merge): every row
    written/updated here gets EconomicCenterId = the Dambulla Markets.Id,
    resolved at runtime BY CODE via _dambulla_market_id() (never a
    hardcoded GUID — GUIDs are per-DB) and cached once per call. Both
    DAMBULLA_DEC and HARTI rows resolve to the same Dambulla market row
    (hub-adjudicated: this legacy splice loader IS Dambulla prices). Raises
    RuntimeError if the Dambulla market row is missing rather than writing
    NULL/a guessed value.

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

    Failure-mode split (by design): if the Markets or CommodityAliases
    lookup itself cannot be loaded (table missing/DB unreachable), the
    _build_market_map()/CommodityAliasResolver construction below raises and
    this whole upsert ABORTS — better to fail loudly than ingest an entire
    run with every row unresolved.  A per-row miss (unknown market name /
    unknown commodity label) is the opposite: WARN + skip / WARN + CropId
    NULL respectively, and ingestion continues.

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
        skipped_invalid_price, crop_resolved, crop_unresolved.
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

        # Shared, source-agnostic row validator (R1.1 P1 Step 5,
        # data_quality.validate_price_row) — non-positive price is
        # REJECTED (row not written at all); min>max is QUARANTINED
        # (written with IsUnitConfirmed=0, never silently swapped). See
        # data_quality.py module docstring Sections 1-2 for full rationale
        # and why this differs from the parser's own upstream lo/hi swap.
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

        # Canonical crop resolution (R1.1 P1 Stage B): resolve via active
        # CommodityAliases (source-scoped 'HARTI' beats a global alias for
        # the same label). Never guess — unresolved stays NULL + WARN, and
        # the row still gets inserted (crop resolution is additive, not a
        # gate on ingestion; heal_price_observation_crops() back-fills it
        # later as aliases are added).
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
            # HARTI's own unit is a verified corpus-wide constant (see
            # canonical.py's fail-closed contract), so this would normally
            # always be True — EXCEPT a min>max hold from validate_price_row
            # above forces it to False (quarantine), overriding the source's
            # own unit-confirmation status. Ambiguous-price rows must not
            # reach the feature layer regardless of unit confidence.
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
                # UPDATE existing row. CropId uses COALESCE so a re-run never
                # overwrites an already-assigned CropId (mirrors the .NET
                # AssignCrop no-silent-re-map contract) -- it can only move
                # NULL -> resolved, exactly like heal_price_observation_crops().
                #
                # IsUnitConfirmed is STICKY-DOWN, never silently raised back
                # up by a routine re-ingest (R1.1 P1 Step 5 reviewer finding,
                # ClickUp 86cahef64): a hold set by flag_price_outliers() or
                # a prior min>max quarantine (IsUnitConfirmed=0) must survive
                # re-running this upsert with the SAME clean parsed row --
                # ingest_harti.py is designed to be re-run routinely
                # (--no-download re-parses is idempotent), and a naive
                # unconditional SET would silently erase the hold the moment
                # the same row is re-parsed clean (validation.hold=False ->
                # is_unit_confirmed=True), defeating the entire quarantine
                # mechanism. The CASE below is a one-way ratchet:
                #   - already 0 (held)      -> stays 0, no matter what the
                #                              incoming row's own flag says.
                #   - currently 1 (cleared) -> takes the incoming value, so
                #                              a genuinely ambiguous re-parse
                #                              (validation.hold=True, e.g. a
                #                              layout regression that starts
                #                              emitting min>max) can still
                #                              LOWER 1 -> 0 on this same
                #                              write -- only RAISING 0 -> 1
                #                              is blocked here.
                # The only way to release an existing hold is the explicit,
                # single-row, parameterized data_quality.clear_outlier_hold()
                # -- never an incidental side effect of re-ingesting.
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
