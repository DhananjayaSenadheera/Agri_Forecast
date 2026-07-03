"""Canonical mapping layer — Python resolution/normalisation side (R1.1 P1
Stage B, ClickUp 86cahef4z).

This module is the single home for cross-source commodity-alias resolution
and the market-dimension dedup rule. It is intentionally source-agnostic
(unlike ``harti/loader.py``, which is HARTI-specific): any future source
writer (CBSL, DEC, ...) resolves crops and filters markets through the same
two entry points defined here, so the resolution/aggregation contract never
forks per source.

Consumes the Stage A (.NET) schema:
  CommodityAliases  — Id, Alias, CropId (FK Crops), Source (NULL = all
                       sources), Language, IsActive, CreatedAtUtc. Unique
                       indexes: (Alias, Source) WHERE Source IS NOT NULL,
                       (Alias) WHERE Source IS NULL.
  PriceObservations.UnitRaw / UnitConversionFactor / IsUnitConfirmed —
                       fail-closed unit quarantine (IsUnitConfirmed defaults
                       to 0/false; unconfirmed rows must never reach
                       features).

================================================================================
1. CROP RESOLUTION (self-heal)
================================================================================
``resolve_crop_id(label, source, engine=...)`` and the batch-oriented
``CommodityAliasResolver`` load ACTIVE CommodityAliases rows and resolve a
raw commodity label + source to a CropId.

Precedence: an exact (Alias, Source) match ALWAYS wins over an (Alias,
Source IS NULL) global match, even though both could theoretically match the
same label — this mirrors the two filtered unique indexes Stage A created
(a source-scoped row and a global row for the same Alias text can coexist,
since they live in different filtered indexes; the source-scoped one is more
specific and takes precedence for that source's ingestion).

Case handling: SQL Server's default collation for this table is
case-INSENSITIVE by design (see CommodityAlias.cs), so "Beans"/"beans"/
"BEANS" must all resolve identically. The Python-side lookup mirrors that by
lower-casing both the loaded alias keys and the incoming label before
dict lookup (rather than re-querying the DB per label) — see
``_normalise_key``. This is a caching decision, not a semantic one: the
resolver loads the full active-alias set ONCE per call/instance (cheap; the
table is small, tens to low-hundreds of rows) and does the match in memory
for every row in a batch, rather than issuing one query per label.

Never guess, never fuzzy-match: a label with no active alias match resolves
to None. Callers must write CropId = NULL and log a WARNING (which is this
project's "ingestion log" — see harti/loader.py and harti/qa.py, there is no
dedicated IngestionLog table; a logged WARNING at the point of the
unresolved row IS the visible gap). Guessing a close-enough crop would
silently corrupt the series — worse than a visible gap.

``heal_price_observation_crops(engine, source=None)`` back-fills CropId on
existing PriceObservations rows where CropId IS NULL and an active alias now
resolves ExternalCommodityName (+ Source). It is idempotent (rows already
healed have CropId set and the WHERE CropId IS NULL guard excludes them on
re-run) and respects IsActive (only active aliases are used to heal — a
deactivated alias must not resurrect a mapping). It intentionally uses the
same one-directional guard as the .NET AssignCrop contract: it ONLY ever
transitions CropId NULL -> a value, and NEVER touches a row that already has
a CropId (no silent re-map of an already-assigned observation — that would
require the explicit overwrite path the .NET side gates behind
``AssignCrop(cropId, overwrite: true)``, which this function deliberately
does not expose).

================================================================================
2. UNIT NORMALISATION + QUARANTINE CONTRACT
================================================================================
HARTI daily wholesale bulletins state prices in Rs./kg (verified corpus-wide
— see harti_multimarket_audit.md, e.g. "Vegetable wholesale price in main
markets on 26/11/2022 (Rs./kg)" — every located market column in the same
table shares that one header-level unit statement). PriceObservations stores
prices ALWAYS as post-conversion LKR/kg.

Contract (module docstring, binding on every source writer):
  A source writer may set IsUnitConfirmed=1 ONLY when:
    (a) the unit is a VERIFIED CONSTANT of that source, corpus-wide — e.g.
        HARTI daily wholesale bulletins are verified Rs/kg for every row
        (UnitRaw='Rs/kg', UnitConversionFactor=1.0), or
    (b) a per-row confirmed conversion has actually been computed and
        applied (UnitConversionFactor reflects the real factor used, and the
        price stored is already the converted LKR/kg figure).
  Anything else — an unverified/unknown unit, a source whose unit statement
  varies row-to-row and hasn't been individually confirmed, or simply not
  having checked — MUST leave IsUnitConfirmed=0 (the column's fail-closed
  default). A quarantined (IsUnitConfirmed=0) row must never reach the
  feature layer; aggregation/feature-build code must filter
  WHERE IsUnitConfirmed = 1.

``HARTI_UNIT_RAW`` / ``HARTI_UNIT_CONVERSION_FACTOR`` are the confirmed
constants for the HARTI source, exported here as the single source of truth
so harti/loader.py (and any future HARTI-adjacent writer) never re-derives
or hardcodes them independently.

================================================================================
3. MARKET-RESOLUTION DEDUP RULE (the S2 trap)
================================================================================
``get_feature_safe_market_ids(engine)`` returns the set of Market IDs
eligible for cross-market aggregation in future feature-builds.

The Pettah double-count trap this guards against: the live Markets
dimension contains (a) MarketType=3 (NationalAggregate) pseudo-markets that
carry an ALREADY-AVERAGED figure across real markets (e.g. a CBSL national
average) — summing/averaging that INTO a cross-market aggregate alongside
the real markets it was itself derived from double-counts the same
underlying price signal; and (b) 'ECOMAP-%'-coded twin markets — synthetic
demo/seed markets used for the Economic-Center-Map UI feature, which are
NOT real HARTI/DEC trading locations and would silently inflate a
cross-market average with fabricated rows if included. Both categories must
be excluded from any query that aggregates "the market" as a set of
independent, non-overlapping physical trading locations. This function is
the one place that exclusion rule lives — feature-build code must call it
rather than re-deriving the WHERE clause ad hoc (which is exactly how a
future S2-style trap would get re-introduced).
"""
from __future__ import annotations

import logging
import uuid
from typing import Iterable

import sqlalchemy as sa

from .db import get_engine

logger = logging.getLogger(__name__)

# --------------------------------------------------------------------------
# Unit contract constants — HARTI daily wholesale bulletins (verified
# corpus-wide; see harti_multimarket_audit.md header excerpt).
# --------------------------------------------------------------------------
HARTI_UNIT_RAW = "Rs/kg"
HARTI_UNIT_CONVERSION_FACTOR = 1.0


def _parse_guid(raw) -> uuid.UUID:
    """Normalise a SQL Server Guid column value (str or bytes) to uuid.UUID.

    Mirrors harti/loader.py::_parse_guid — pymssql may return a Guid as a
    string or as raw bytes depending on driver/column config, and
    byte-ordered Guids from SQL Server are little-endian.
    """
    if isinstance(raw, (bytes, bytearray)):
        return uuid.UUID(bytes_le=raw)
    return uuid.UUID(str(raw))


def _normalise_key(alias: str, source: "str | None") -> tuple[str, "str | None"]:
    """Case-insensitive lookup key, mirroring the DB's case-insensitive
    collation on CommodityAliases.Alias (see CommodityAlias.cs). Source is
    compared as-is (case-sensitive) — Source is a controlled code value
    ("HARTI", ...), not free text, and every writer in this codebase passes
    it as a fixed literal, so case drift there is not a real-world risk the
    way alias-text case drift is.

    ASCII assumption: str.lower() only equals the DB's CI collation for
    ASCII alias text, which is all we store today (English labels; future
    Sinhala/Tamil romanizations are ASCII too). If a non-ASCII alias ever
    diverges, the failure mode is a MISS (CropId NULL + WARN, healable
    later) — never a wrong-crop match; the DB unique indexes stay the real
    integrity guard.
    """
    return (alias.strip().lower(), source)


class CommodityAliasResolver:
    """Loads active CommodityAliases once and resolves (label, source) pairs
    in memory for the duration of one call/instance — the "cache-per-run"
    pattern used elsewhere in this package (see
    harti/loader.py::_build_market_map).

    Precedence: an exact (Alias, Source) match wins over an (Alias, NULL)
    global match. Unresolved labels return None — never a guess, never a
    fuzzy match.
    """

    def __init__(self, engine: sa.engine.Engine):
        self._by_source: dict[tuple[str, str], uuid.UUID] = {}
        self._global: dict[str, uuid.UUID] = {}
        self._load(engine)

    def _load(self, engine: sa.engine.Engine) -> None:
        with engine.connect() as conn:
            rows = conn.execute(sa.text(
                """SELECT Alias, Source, CropId
                   FROM CommodityAliases
                   WHERE IsActive = 1"""
            )).fetchall()

        n_source_scoped = 0
        n_global = 0
        for alias, source, crop_id_raw in rows:
            crop_id = _parse_guid(crop_id_raw)
            norm_alias, _ = _normalise_key(alias, None)
            if source is None:
                self._global[norm_alias] = crop_id
                n_global += 1
            else:
                self._by_source[(norm_alias, source)] = crop_id
                n_source_scoped += 1

        logger.info(
            "CommodityAliasResolver loaded: %d source-scoped, %d global active aliases",
            n_source_scoped, n_global,
        )

    def resolve(self, label: str, source: "str | None" = None) -> "uuid.UUID | None":
        """Resolve a raw commodity label (+ optional source) to a CropId, or
        None if no active alias matches. Source-scoped match takes
        precedence over a global match for the same label.
        """
        if not label:
            return None
        norm_alias, _ = _normalise_key(label, None)

        if source is not None:
            hit = self._by_source.get((norm_alias, source))
            if hit is not None:
                return hit

        return self._global.get(norm_alias)


def resolve_crop_id(
    label: str,
    source: "str | None" = None,
    *,
    engine: "sa.engine.Engine | None" = None,
) -> "uuid.UUID | None":
    """Convenience one-shot resolution (builds a fresh resolver each call).

    Batch callers (e.g. a loader processing hundreds of rows per run) should
    build a ``CommodityAliasResolver`` once instead and call ``.resolve()``
    per row — this function is for call sites that only need a single
    lookup and don't want to manage the resolver's lifetime themselves.
    """
    eng = engine if engine is not None else get_engine()
    return CommodityAliasResolver(eng).resolve(label, source)


def heal_price_observation_crops(
    engine: "sa.engine.Engine | None" = None,
    *,
    source: "str | None" = None,
    dry_run: bool = False,
) -> dict:
    """Back-fill CropId on PriceObservations rows where CropId IS NULL and
    an active CommodityAlias now resolves ExternalCommodityName (+ Source).

    Idempotent: only touches CropId IS NULL rows (WHERE guard below), so a
    second run over already-healed rows is a no-op — it never re-examines,
    let alone re-maps, a row that already carries a CropId. This mirrors the
    .NET AssignCrop contract in spirit: the heal path is the "no overwrite"
    call, never the explicit ``overwrite: true`` re-map path.

    Respects IsActive: resolution only considers active aliases (identical
    resolver to resolve_crop_id/CommodityAliasResolver), so a deactivated
    alias cannot heal a row.

    Args:
        engine:   SQLAlchemy engine; created from config if None.
        source:   If given, only heal rows for this Source (e.g. "HARTI").
                  If None, heal across all sources.
        dry_run:  If True, resolve and report counts but do not write.

    Returns:
        Dict with counts: candidates (NULL-CropId rows examined), healed
        (rows that got a CropId), unresolved (still NULL — no active alias
        matched; logged as WARN, one per distinct unresolved label so a
        large backlog doesn't flood the log).
    """
    eng = engine if engine is not None else get_engine()
    resolver = CommodityAliasResolver(eng)

    where_source = "AND Source = :source" if source is not None else ""
    params: dict = {"source": source} if source is not None else {}

    with eng.connect() as conn:
        rows = conn.execute(sa.text(
            f"""SELECT Id, ExternalCommodityName, Source
                FROM PriceObservations
                WHERE CropId IS NULL
                {where_source}"""
        ), params).fetchall()

    counters = dict(candidates=len(rows), healed=0, unresolved=0)

    to_heal: list[dict] = []
    unresolved_labels: set[tuple[str, str]] = set()
    for row_id, ext_name, row_source in rows:
        crop_id = resolver.resolve(ext_name, row_source)
        if crop_id is None:
            counters["unresolved"] += 1
            unresolved_labels.add((ext_name, row_source))
            continue
        to_heal.append({"id": row_id, "crop_id": str(crop_id)})

    for ext_name, row_source in sorted(unresolved_labels):
        logger.warning(
            "heal_price_observation_crops: no active alias for label %r "
            "(source=%r) — CropId left NULL, never guessed",
            ext_name, row_source,
        )

    logger.info(
        "heal_price_observation_crops: %d candidates, %d resolvable, %d unresolved",
        counters["candidates"], len(to_heal), counters["unresolved"],
    )

    if dry_run or not to_heal:
        counters["healed"] = 0 if not to_heal else len(to_heal) if dry_run else 0
        if dry_run:
            logger.info("DRY RUN — skipping DB writes")
        return counters

    with eng.begin() as conn:
        for row in to_heal:
            # Guard belt-and-braces at the SQL level too: only ever flips a
            # NULL CropId to a value, never re-maps an already-assigned row,
            # even under concurrent writers.
            conn.execute(sa.text(
                """UPDATE PriceObservations
                   SET CropId = :crop_id
                   WHERE Id = :id
                     AND CropId IS NULL"""
            ), {"crop_id": row["crop_id"], "id": row["id"]})

    counters["healed"] = len(to_heal)
    logger.info("heal_price_observation_crops: healed %d rows", counters["healed"])
    return counters


# --------------------------------------------------------------------------
# Market-resolution dedup rule (the S2 trap)
# --------------------------------------------------------------------------

# MarketType=3 -- NationalAggregate (see AgriForecast.Domain.Enums.MarketType).
# A synthetic pseudo-market carrying an already-averaged national figure
# (e.g. CBSL national average); not a location, must never be mixed into
# location-level cross-market aggregation.
_NATIONAL_AGGREGATE_MARKET_TYPE = 3

# ECOMAP-coded twin markets are synthetic demo/seed rows for the Economic-
# Center-Map UI feature -- not real HARTI/DEC trading locations.
_ECOMAP_MARKET_CODE_PREFIX = "ECOMAP-"


def get_feature_safe_market_ids(
    engine: "sa.engine.Engine | None" = None,
) -> set[uuid.UUID]:
    """Return Market IDs eligible for cross-market aggregation in future
    feature-builds.

    Excludes:
      - MarketType = 3 (NationalAggregate): an already-averaged pseudo-market
        (e.g. CBSL national average). Including it in a cross-market
        aggregate alongside the real markets it was itself derived from
        double-counts the same underlying price signal (the Pettah
        double-count trap this whole function exists to prevent).
      - MarketCode LIKE 'ECOMAP-%': synthetic demo/seed twin markets for the
        Economic-Center-Map UI feature, not real trading locations.

    Any code that aggregates "the market" as a set of independent physical
    trading locations MUST filter through this function rather than
    re-deriving the WHERE clause ad hoc.
    """
    eng = engine if engine is not None else get_engine()
    with eng.connect() as conn:
        rows = conn.execute(sa.text(
            """SELECT Id
               FROM Markets
               WHERE MarketType <> :national_aggregate
                 AND MarketCode NOT LIKE :ecomap_prefix"""
        ), {
            "national_aggregate": _NATIONAL_AGGREGATE_MARKET_TYPE,
            "ecomap_prefix": f"{_ECOMAP_MARKET_CODE_PREFIX}%",
        }).fetchall()

    result = {_parse_guid(r[0]) for r in rows}
    logger.info("get_feature_safe_market_ids: %d feature-safe markets", len(result))
    return result
