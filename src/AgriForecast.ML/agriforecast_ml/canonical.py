"""Canonical mapping layer: crop-alias resolution and the market dedup rule.

Source-agnostic, unlike harti/loader.py: every source writer (HARTI, DEC, CBSL)
resolves crops and filters markets through the two entry points here, so the
contract never forks per source.

Crop resolution
  resolve_crop_id() and CommodityAliasResolver look up ACTIVE CommodityAliases
  rows. An exact (Alias, Source) match always beats an (Alias, Source IS NULL)
  global match. Alias text is matched case-insensitively to mirror the DB
  collation. A label with no active alias resolves to None - never guess, never
  fuzzy-match; the caller writes CropId = NULL and logs a WARNING, because a
  visible gap beats a silently corrupted series. heal_price_observation_crops()
  back-fills CropId on existing rows and only ever moves NULL -> a value.

Unit contract (binding on every source writer)
  PriceObservations always stores LKR/kg. Set IsUnitConfirmed=1 only when the
  unit is a verified constant of that source (HARTI bulletins are Rs/kg
  corpus-wide) or a real per-row conversion was applied. Anything else stays 0,
  and a row with IsUnitConfirmed=0 must never reach the feature layer.

Market dedup
  get_feature_safe_market_ids() excludes NationalAggregate pseudo-markets (an
  already-averaged figure, so including it double-counts) and synthetic ECOMAP-%
  twin markets. Aggregate through it instead of re-deriving the WHERE clause.
"""
from __future__ import annotations

import logging
import uuid
from typing import Iterable

import sqlalchemy as sa

from .db import get_engine

logger = logging.getLogger(__name__)

# Unit constants for HARTI bulletins: verified Rs/kg corpus-wide.
HARTI_UNIT_RAW = "Rs/kg"
HARTI_UNIT_CONVERSION_FACTOR = 1.0

# The Dambulla DEC API is confirmed Rs/kg; dec_mirror.py is its only writer.
DEC_UNIT_RAW = "Rs/kg"
DEC_UNIT_CONVERSION_FACTOR = 1.0


def _parse_guid(raw) -> uuid.UUID:
    """Normalise a SQL Server Guid column value (str or bytes) to uuid.UUID.

    pymssql returns a Guid as a string or as little-endian bytes depending on the
    driver, so both are handled. Mirrors harti/loader.py::_parse_guid.
    """
    if isinstance(raw, (bytes, bytearray)):
        return uuid.UUID(bytes_le=raw)
    return uuid.UUID(str(raw))


def _normalise_key(alias: str, source: "str | None") -> tuple[str, "str | None"]:
    """Case-insensitive lookup key, mirroring the DB collation on CommodityAliases.Alias.

    Source is compared as-is: it is a controlled code value, not free text.
    str.lower() only matches the DB collation for ASCII aliases; a non-ASCII mismatch
    would miss (CropId NULL + WARN), never resolve to the wrong crop.
    """
    return (alias.strip().lower(), source)


class CommodityAliasResolver:
    """Loads active CommodityAliases once and resolves (label, source) pairs in memory.

    An exact (Alias, Source) match wins over an (Alias, NULL) global match.
    Unresolved labels return None - never a guess, never a fuzzy match.
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
        """Resolve a label (+ optional source) to a CropId, or None if no active alias matches."""
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
    """One-shot resolution; builds a fresh resolver on every call.

    Batch callers should build one CommodityAliasResolver and call .resolve() per row.
    """
    eng = engine if engine is not None else get_engine()
    return CommodityAliasResolver(eng).resolve(label, source)


def heal_price_observation_crops(
    engine: "sa.engine.Engine | None" = None,
    *,
    source: "str | None" = None,
    dry_run: bool = False,
) -> dict:
    """Back-fill CropId on PriceObservations rows where it is still NULL.

    Idempotent: only CropId IS NULL rows are touched, so an already-assigned row is
    never re-mapped. Only active aliases can heal a row.

    Args:
        engine:   SQLAlchemy engine; created from config if None.
        source:   Only heal rows for this Source (e.g. 'HARTI'); None heals all.
        dry_run:  Resolve and report counts without writing.

    Returns:
        Counts of candidates examined, rows healed and rows left unresolved.
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
            # The SQL repeats the guard: only NULL -> value, never a re-map, even under
            # concurrent writers.
            conn.execute(sa.text(
                """UPDATE PriceObservations
                   SET CropId = :crop_id
                   WHERE Id = :id
                     AND CropId IS NULL"""
            ), {"crop_id": row["crop_id"], "id": row["id"]})

    counters["healed"] = len(to_heal)
    logger.info("heal_price_observation_crops: healed %d rows", counters["healed"])
    return counters



# MarketType 3 = NationalAggregate (mirrors AgriForecast.Domain.Enums.MarketType):
# an already-averaged figure, never mixed into location-level aggregation.
_NATIONAL_AGGREGATE_MARKET_TYPE = 3

# ECOMAP-coded markets are synthetic demo rows, not real trading locations.
_ECOMAP_MARKET_CODE_PREFIX = "ECOMAP-"


def get_feature_safe_market_ids(
    engine: "sa.engine.Engine | None" = None,
) -> set[uuid.UUID]:
    """Market IDs that are safe to use for cross-market aggregation.

    Excludes MarketType 3 (NationalAggregate), which is already an average and would
    double-count the same prices, and MarketCode LIKE 'ECOMAP-%', which are synthetic
    demo markets. Any code aggregating across markets must filter through this rather
    than writing its own WHERE clause.
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


def resolve_market_id_by_code(
    engine: "sa.engine.Engine | None" = None,
    *,
    market_code: str,
) -> uuid.UUID:
    """Resolve a Markets.Id by MarketCode - never a hardcoded GUID, GUIDs are per-DB.

    Raises RuntimeError if no market has this code, rather than writing a guess.
    """
    eng = engine if engine is not None else get_engine()
    with eng.connect() as conn:
        row = conn.execute(
            sa.text("SELECT Id FROM Markets WHERE MarketCode = :code"),
            {"code": market_code},
        ).fetchone()

    if row is None:
        raise RuntimeError(
            f"Market row not found (MarketCode={market_code!r}) — refusing to "
            "write with a NULL/guessed MarketId."
        )
    return _parse_guid(row[0])
