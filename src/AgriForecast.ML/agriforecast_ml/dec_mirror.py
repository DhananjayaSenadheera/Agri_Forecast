"""DEC -> PriceObservations mirror.

Mirrors MarketPrices rows with Source='DAMBULLA_DEC' into PriceObservations, so that table
becomes the single refined layout for every price source. It reads MarketPrices and writes
PriceObservations only: MarketPrices is never touched, and the ML training frame is
unchanged because load.load_price_observations() still filters Source='HARTI'.

Set-based, not row-by-row: the backfill is about 33.6k candidate rows and the HARTI
loader's per-row upsert manages roughly 37 rows/s, so this issues a single
INSERT ... SELECT ... WHERE NOT EXISTS instead.

Insert-only, with no UPDATE branch: DEC's own MarketPrices ingestion is insert-only and
never revises an existing row's prices, so the mirror inherits that write-once guarantee
and has nothing to reconcile on a re-run except genuinely new rows.

Identity. PriceObservations has two filtered unique indexes, one keyed on
ExternalCommodityId and one on ExternalCommodityName. HARTI is name-keyed (it always
writes a NULL ExternalCommodityId), so this mirror uses the id-keyed index with
ExternalCommodityId = MarketPrices.ExternalProductId. Passion has two DEC ext-ids that map
to one crop, and since the index is scoped on the ext-id they insert as two rows per date -
the same duplication load_prices() already mean-collapses at ML-load time. CropId is read
live on every run, so the mirror automatically follows any upstream re-mapping.

Field mapping worth knowing: MarketId is resolved by MarketCode at runtime, never trusted
from MarketPrices.EconomicCenterId. Wholesale/Retail prices and ArrivalsKg are NULL, as for
HARTI. The unit is Rs/kg with factor 1.0 from canonical.py. IsUnitConfirmed is 1 unless
MinPrice > MaxPrice, which is quarantined with 0 rather than silently swapped.
RetrievedAtUtc is SYSUTCDATETIME(), i.e. when this mirror pass ran.

Non-positive prices are REJECTED (WHERE MinPrice > 0 AND MaxPrice > 0) - the same rule
data_quality.validate_price_row() applies, expressed in SQL so the statement stays
set-based. The 196 known zero-price DEC rows stay in MarketPrices as the market-closed
signal but are never mirrored: in PriceObservations, missingness is an absent row.

NULL ExternalProductId rows are excluded as defence in depth. SQL's NULL = NULL is never
true, so such a row would look unmirrored on every run, would be written against the
NAME-keyed index instead, and the second run would collide there and roll back that whole
day's insert. They are counted in dry_run_report()'s skipped_null_extid, not dropped
silently.

AsOfUtc is ObservedDate + 1 day at 00:30 UTC (06:00 Sri Lanka time), NOT
MarketPrices.RetrievedAtUtc. RetrievedAtUtc records when our own backfill happened to pull
a date, not when the market published it: every PriceDate=2025-05-05 row carries
RetrievedAtUtc=2026-02-19, so using it would falsely claim a year of live prices was
unknowable until then. The formula is the same conservative-late fallback HARTI's loader
uses, so it can only delay a point-in-time join, never leak.
"""
from __future__ import annotations

import logging
from datetime import date
from typing import Optional

import sqlalchemy as sa

from .canonical import (
    DEC_UNIT_CONVERSION_FACTOR,
    DEC_UNIT_RAW,
    resolve_market_id_by_code,
)
from .db import get_engine

logger = logging.getLogger(__name__)

SOURCE = "DAMBULLA_DEC"
MARKET_PRICES_SOURCE = "DAMBULLA_DEC"  # MarketPrices.Source value we mirror FROM

# The Dambulla Markets.Id is resolved at runtime, never a hardcoded GUID (same rule as
# harti/loader.py and the .NET ingestion service).
DAMBULLA_MARKET_CODE = "MKT00000001"


# Shared WHERE fragment: which MarketPrices rows are eligible to mirror. Used by both the
# COUNT (dry run) and the INSERT below, so the two can never drift apart.
_ELIGIBLE_WHERE = """
    mp.Source = :dec_source
    AND mp.CropId IS NOT NULL
    AND mp.ExternalProductId IS NOT NULL
    AND mp.MinPrice > 0
    AND mp.MaxPrice > 0
"""

_SINCE_CLAUSE = "AND mp.PriceDate > :since_date"

# Anti-join against the id-keyed filtered unique index
# (MarketId, ExternalCommodityId, ObservedDate, Source) WHERE ExternalCommodityId IS NOT NULL.
_NOT_EXISTS = """
    AND NOT EXISTS (
        SELECT 1 FROM PriceObservations po
        WHERE po.MarketId = :market_id
          AND po.ExternalCommodityId = mp.ExternalProductId
          AND po.ObservedDate = mp.PriceDate
          AND po.Source = :dec_source
    )
"""


def _base_params(market_id, since_date: "date | None") -> dict:
    params: dict = {"dec_source": SOURCE, "market_id": str(market_id)}
    if since_date is not None:
        params["since_date"] = since_date
    return params


def _where(*, since: bool, not_exists: bool) -> str:
    clauses = [_ELIGIBLE_WHERE]
    if since:
        clauses.append(_SINCE_CLAUSE)
    if not_exists:
        clauses.append(_NOT_EXISTS)
    return "\n".join(clauses)


def dry_run_report(
    engine: "sa.engine.Engine | None" = None,
    *,
    since_date: "date | None" = None,
) -> dict:
    """Read-only counts: what would mirror_dec_to_price_observations() do?

    Never writes, so it is safe to run against the live DB at any time.

    Returns:
        market_id, eligible_total (DEC rows passing the fail-closed gate, before dedup
        against PriceObservations), already_mirrored, would_insert, skipped_non_positive,
        skipped_null_crop, skipped_null_extid, min_observed_date, max_observed_date and
        distinct_crops.
    """
    eng = engine if engine is not None else get_engine()
    market_id = resolve_market_id_by_code(eng, market_code=DAMBULLA_MARKET_CODE)
    params = _base_params(market_id, since_date)

    with eng.connect() as conn:
        eligible_total = conn.execute(sa.text(
            f"SELECT COUNT(*) FROM MarketPrices mp WHERE {_where(since=since_date is not None, not_exists=False)}"
        ), params).scalar() or 0

        would_insert = conn.execute(sa.text(
            f"SELECT COUNT(*) FROM MarketPrices mp WHERE {_where(since=since_date is not None, not_exists=True)}"
        ), params).scalar() or 0

        skipped_non_positive = conn.execute(sa.text(
            """SELECT COUNT(*) FROM MarketPrices mp
               WHERE mp.Source = :dec_source
                 AND (mp.MinPrice <= 0 OR mp.MaxPrice <= 0)"""
            + (" AND mp.PriceDate > :since_date" if since_date is not None else "")
        ), params).scalar() or 0

        skipped_null_crop = conn.execute(sa.text(
            """SELECT COUNT(*) FROM MarketPrices mp
               WHERE mp.Source = :dec_source
                 AND mp.CropId IS NULL"""
            + (" AND mp.PriceDate > :since_date" if since_date is not None else "")
        ), params).scalar() or 0

        skipped_null_extid = conn.execute(sa.text(
            """SELECT COUNT(*) FROM MarketPrices mp
               WHERE mp.Source = :dec_source
                 AND mp.ExternalProductId IS NULL"""
            + (" AND mp.PriceDate > :since_date" if since_date is not None else "")
        ), params).scalar() or 0

        date_row = conn.execute(sa.text(
            f"""SELECT MIN(mp.PriceDate), MAX(mp.PriceDate), COUNT(DISTINCT mp.CropId)
                FROM MarketPrices mp WHERE {_where(since=since_date is not None, not_exists=False)}"""
        ), params).fetchone()

    report = {
        "market_id": str(market_id),
        "eligible_total": int(eligible_total),
        "already_mirrored": int(eligible_total) - int(would_insert),
        "would_insert": int(would_insert),
        "skipped_non_positive": int(skipped_non_positive),
        "skipped_null_crop": int(skipped_null_crop),
        "skipped_null_extid": int(skipped_null_extid),
        "min_observed_date": date_row[0].isoformat() if date_row and date_row[0] else None,
        "max_observed_date": date_row[1].isoformat() if date_row and date_row[1] else None,
        "distinct_crops": int(date_row[2]) if date_row else 0,
    }
    logger.info("dec_mirror.dry_run_report: %s", report)
    return report


def mirror_dec_to_price_observations(
    engine: "sa.engine.Engine | None" = None,
    *,
    since_date: "date | None" = None,
    dry_run: bool = False,
) -> dict:
    """Mirror MarketPrices(Source='DAMBULLA_DEC') rows into PriceObservations.

    A single set-based INSERT ... SELECT ... WHERE NOT EXISTS: idempotent and insert-only, so
    re-running with no new MarketPrices rows returns inserted=0.

    Args:
        engine:      SQLAlchemy engine; created from config if None.
        since_date:  Only consider MarketPrices rows with PriceDate strictly after this date.
                     The anti-join is already idempotent without it, so this only keeps the
                     daily pass from re-scanning the whole table. None means full backfill.
        dry_run:     Resolve and count, but issue no writes.

    Returns the dry_run_report() keys plus inserted, the rows actually written this call.
    """
    eng = engine if engine is not None else get_engine()

    # dry_run_report() resolves the market id itself -- reuse its result
    # (report["market_id"]) rather than resolving it a second time here.
    report = dry_run_report(eng, since_date=since_date)

    if dry_run:
        report["inserted"] = 0
        return report

    if report["would_insert"] == 0:
        logger.info("dec_mirror: nothing to insert (0 candidate rows)")
        report["inserted"] = 0
        return report

    params = _base_params(report["market_id"], since_date)

    insert_sql = sa.text(
        f"""
        INSERT INTO PriceObservations
            (Id, MarketId, CropId, ExternalCommodityId, ExternalCommodityName,
             ObservedDate, WholesalePrice, RetailPrice, MinPrice, MaxPrice,
             ArrivalsKg, AsOfUtc, Source, RetrievedAtUtc,
             UnitRaw, UnitConversionFactor, IsUnitConfirmed)
        SELECT
            NEWID(),
            :market_id,
            mp.CropId,
            mp.ExternalProductId,
            mp.ExternalProductName,
            mp.PriceDate,
            NULL,
            NULL,
            mp.MinPrice,
            mp.MaxPrice,
            NULL,
            -- AsOfUtc: ObservedDate + 1 day, 00:30 UTC (== 06:00 Sri Lanka
            -- time) -- see module docstring "AsOfUtc" section. Deliberately
            -- NOT mp.RetrievedAtUtc (proven unreliable for historical rows).
            DATEADD(MINUTE, 30, CAST(DATEADD(DAY, 1, mp.PriceDate) AS DATETIME2)),
            :dec_source,
            SYSUTCDATETIME(),
            :unit_raw,
            :unit_factor,
            CASE WHEN mp.MinPrice > mp.MaxPrice THEN 0 ELSE 1 END
        FROM MarketPrices mp
        WHERE {_where(since=since_date is not None, not_exists=True)}
        """
    )
    insert_params = dict(params)
    insert_params["unit_raw"] = DEC_UNIT_RAW
    insert_params["unit_factor"] = DEC_UNIT_CONVERSION_FACTOR

    with eng.begin() as conn:
        result = conn.execute(insert_sql, insert_params)
        inserted = result.rowcount if result.rowcount is not None and result.rowcount >= 0 else report["would_insert"]

    report["inserted"] = int(inserted)
    logger.info("dec_mirror.mirror_dec_to_price_observations: inserted=%d (would_insert=%d)",
                report["inserted"], report["would_insert"])
    return report
