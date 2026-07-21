"""DEC -> PriceObservations mirror (R2 Step 2, ClickUp 86cajtx2k).

Mirrors ``MarketPrices`` rows with ``Source='DAMBULLA_DEC'`` into
``PriceObservations`` so PriceObservations becomes the single "common refined
layout" for ALL price sources (today it holds only ``Source='HARTI'`` rows).
This is a READ from MarketPrices + WRITE into PriceObservations only —
MarketPrices itself is never touched, and the ML training frame is
UNCHANGED by this mirror (see ``load.load_price_observations``'s explicit
``Source='HARTI'`` filter, added alongside this module, which freezes today's
CropFeatureDaily identity; see CONTRACTS.md / DECISIONS.md 2026-07 R2 Step 2).

============================================================================
WHY SET-BASED, NOT ROW-BY-ROW
============================================================================
harti/loader.py's ``upsert_harti_price_observations`` upserts one row at a
time in a Python loop (~37 rows/s on the live corpus — a known, previously
reported pain point). The DEC backfill is ~33.6k candidate rows; row-by-row
would take tens of minutes under table locks. This module instead issues a
SINGLE set-based ``INSERT ... SELECT ... WHERE NOT EXISTS`` statement (the
SQL Server equivalent of an anti-join upsert) so the whole backfill is one
INSERT, and the daily incremental run (a few dozen new rows/day) is trivially
fast and lock-light.

============================================================================
WHY INSERT-ONLY (NO UPDATE BRANCH)
============================================================================
Unlike HARTI bulletins (which can be re-parsed and occasionally correct a
previously-loaded value), DEC's own MarketPrices ingestion
(MarketPriceIngestionService.cs) is INSERT-ONLY and idempotent by
construction: ``if (existingDates.Contains(date)) { skipped++; continue; }``
-- once a (Source, ExternalProductId, PriceDate) row exists in MarketPrices,
its MinPrice/MaxPrice are NEVER revised by later ingestion passes. The mirror
inherits that same write-once guarantee, so a plain anti-join INSERT (no
UPDATE branch, no "sticky-down" ratchet like HARTI's IsUnitConfirmed CASE
WHEN) is fully correct here -- there is nothing to reconcile on a re-run
besides genuinely NEW rows.

============================================================================
IDENTITY KEY / UNIQUE INDEX MAPPING
============================================================================
PriceObservations has TWO filtered unique indexes (AgriForecastDbContext.cs):
  UX_PriceObservations_MarketCommodityIdDateSource
    (MarketId, ExternalCommodityId, ObservedDate, Source) WHERE ExternalCommodityId IS NOT NULL
  UX_PriceObservations_MarketCommodityNameDateSource
    (MarketId, ExternalCommodityName, ObservedDate, Source) WHERE ExternalCommodityId IS NULL

HARTI is name-keyed (always writes ExternalCommodityId = NULL, live-verified:
0 of 375,432 existing rows have a non-NULL ExternalCommodityId), so this
mirror uses the ID-KEYED index: ExternalCommodityId = MarketPrices.
ExternalProductId (the DEC feed's own stable per-commodity id, range 1-101,
live-verified). This is the natural, collision-free choice -- DEC never
shares that index with anything else today.

PASSION (by-design duplicate): two DEC ExternalProductIds (43 and 76) both
resolve to CropId FRT000019 (live-verified, 396 rows each side). The
id-keyed unique index is scoped on (MarketId, ExternalCommodityId, ...), NOT
CropId, so both ext-ids insert as two SEPARATE PriceObservations rows per
date for Passion -- intentional, mirrors how load_prices() already handles
this same crop's MarketPrices duplication (mean-collapsed at ML-load time,
see load.py / tests/test_price_dedup.py). No special-casing needed here.

CropId IS READ AT MIRROR-TIME (never frozen): the mirror's SELECT reads
MarketPrices.CropId live on every run, so it automatically follows any
upstream re-mapping (e.g. the pending FixGarlicAliasMapping migration that
repoints ext-id 47 from Big Onion to a new Garlic crop) without this module
ever needing to change.

============================================================================
FIELD MAPPING
============================================================================
  PriceObservations.MarketId               <- resolved by MarketCode (Dambulla,
                                               MKT00000001), NEVER trusted from
                                               MarketPrices.EconomicCenterId
                                               (same fail-closed-resolve-at-
                                               runtime rule as harti/loader.py).
  PriceObservations.CropId                 <- MarketPrices.CropId (as-read-at-
                                               mirror-time; see above).
  PriceObservations.ExternalCommodityId    <- MarketPrices.ExternalProductId.
  PriceObservations.ExternalCommodityName  <- MarketPrices.ExternalProductName.
  PriceObservations.ObservedDate           <- MarketPrices.PriceDate.
  PriceObservations.MinPrice / MaxPrice    <- MarketPrices.MinPrice / MaxPrice
                                               (DEC has no Wholesale/Retail
                                               columns at all -- MarketPrices
                                               only ever carries Min/Max, so
                                               there is nothing to lose here).
  PriceObservations.WholesalePrice/RetailPrice <- NULL (same convention as
                                               HARTI -- see harti/loader.py's
                                               "PRICE COLUMNS" note).
  PriceObservations.ArrivalsKg             <- NULL. (Note:
                                               PriceObservations.ArrivalsKg is
                                               a known stub column, 0/N
                                               populated corpus-wide -- see
                                               2026-07-04 P4 lesson -- do not
                                               build anything on it.)
  PriceObservations.Source                 <- 'DAMBULLA_DEC' (verbatim).
  PriceObservations.UnitRaw/ConversionFactor <- canonical.DEC_UNIT_RAW /
                                               DEC_UNIT_CONVERSION_FACTOR
                                               ("Rs/kg", 1.0) -- DEC's
                                               MinPrice/MaxPrice are already
                                               LKR/kg (same as HARTI; no
                                               source states any other unit
                                               anywhere in the DEC feed/UI).
  PriceObservations.IsUnitConfirmed        <- 1, UNLESS MinPrice > MaxPrice
                                               (defensive; live-verified 0
                                               occurrences in the DEC corpus
                                               today, but kept for parity
                                               with data_quality.
                                               validate_price_row's min>max
                                               HOLD contract) in which case 0
                                               (quarantined, never silently
                                               swapped).
  PriceObservations.AsOfUtc                <- ObservedDate + 1 day, 00:30 UTC
                                               (== 06:00 Sri Lanka time) --
                                               see "AsOfUtc" section below.
                                               NOT MarketPrices.RetrievedAtUtc.
  PriceObservations.RetrievedAtUtc         <- SYSUTCDATETIME() (when THIS
                                               mirror pass ran; audit-only,
                                               mirrors harti/loader.py's
                                               now_utc stamp).

============================================================================
NON-POSITIVE PRICE HANDLING (fail-closed, matches HARTI's contract)
============================================================================
data_quality.validate_price_row()'s shared contract says non-positive price
-> REJECT (do not write the row at all) -- "a fabricated zero price sitting
in a price column" is exactly the explicit-missingness violation this
project's disciplines forbid. Live-verified: 196 DAMBULLA_DEC MarketPrices
rows carry MinPrice=MaxPrice=0.00 (all dated 2025-05-05, the DEC launch day;
0 "mixed" rows where only one of Min/Max is <=0; 0 MinPrice>MaxPrice rows in
the whole DEC corpus). This mirror applies the SAME reject rule at the SQL
level (``WHERE mp.MinPrice > 0 AND mp.MaxPrice > 0``) rather than importing
validate_price_row() row-by-row (which would force a Python loop and defeat
the whole point of being set-based) -- the predicate is identical in effect,
just expressed as a SQL WHERE clause instead of a per-row Python call. Those
196 rows stay in MarketPrices untouched (kept there as the market-closed
signal, per this project's MarketPrices zero-price convention) but are never
mirrored into PriceObservations, matching HARTI's convention that
PriceObservations itself has NO zero-price rows (missingness there is an
ABSENT row, not a stored zero).

============================================================================
NULL ExternalProductId GUARD (defense-in-depth, SF1 reviewer should-fix)
============================================================================
``_ELIGIBLE_WHERE`` also requires ``mp.ExternalProductId IS NOT NULL``. Live-
verified: 0 such rows exist in the DEC corpus today, so this is pure
defense-in-depth, not a fix for an observed problem -- but it closes a real
failure mode. ``_NOT_EXISTS``'s anti-join compares
``po.ExternalCommodityId = mp.ExternalProductId``; SQL's three-valued logic
means NULL = NULL is never TRUE, so a hypothetical future NULL-ext-id DEC row
would make that comparison never match ANY existing PriceObservations row --
the anti-join would treat it as "not yet mirrored" on EVERY run, and the
INSERT would try to write it as a NAME-keyed row (ExternalCommodityId NULL
routes to ``UX_PriceObservations_MarketCommodityNameDateSource`` instead of
the id-keyed index this module targets). The SECOND such run would then hit
that unique index's own (MarketId, ExternalCommodityName, ObservedDate,
Source) collision and raise -- inside the SAME multi-row ``INSERT...SELECT``
as every other still-legitimate row that run, rolling the whole daily insert
back. Excluding NULL ext-ids up front removes the failure mode entirely
rather than relying on a would-be constraint violation to catch it later.
Counted (not silently dropped) via ``dry_run_report()``'s
``skipped_null_extid``.

============================================================================
AsOfUtc -- WHY NOT MarketPrices.RetrievedAtUtc
============================================================================
The obvious candidate for AsOfUtc would be MarketPrices.RetrievedAtUtc ("when
our ingestion fetched it" -- exactly PriceObservation.cs's own docstring
definition of that concept). Live-verified this is WRONG for DEC: DEC's
RetrievedAtUtc reflects the calendar day OUR ingestion pipeline happened to
first pull a given date into the DB, not the day the Dambulla market/DEC feed
actually published it. The live corpus has only 13 distinct RetrievedAtUtc
calendar dates across 33,816 rows -- i.e. a handful of large historical
backfill batches, not one-row-per-day capture. Concretely: every PriceDate=
2025-05-05 row (the DEC launch date) carries RetrievedAtUtc=2026-02-19 --
nearly 10 MONTHS later, because that is when a historical-backfill pass first
pulled that date's chart data into MarketPrices, not because the price was
somehow unknown to the market itself until then. Using RetrievedAtUtc
directly as AsOfUtc would falsely encode "this price wasn't knowable until
Feb 2026" for over a year of real, live-published market history -- the
opposite of point-in-time-correct. (Recent rows ARE same-day: the last ~2
months show RetrievedAtUtc == PriceDate, i.e. the daily ingestion job is now
running close to real-time -- but that recency does not retroactively fix
the older batch-backfilled rows' timestamps.)

Instead this mirror uses the SAME conservative-late fallback formula HARTI's
loader uses when it has no better vintage signal (_resolve_as_of_utc's
fallback branch): ObservedDate + 1 day at 06:00 Sri Lanka time (+05:30) =
ObservedDate+1 00:30 UTC. Sri Lanka's Dambulla DEC price feed is understood
to publish same-day/next-morning like HARTI's own bulletins (both are daily
government/quasi-government market-price feeds on a similar cadence), so the
market's true publication timing is genuinely close to this fallback -- it is
our OWN system's ingestion history that is unreliable here, not the
underlying source's cadence. This choice is conservative-LATE (never
early), so it can only delay a future point-in-time join, never leak.

NOTE: today (this task, R2 Step 2) NO feature consumer reads Source=
'DAMBULLA_DEC' PriceObservations rows at all (load.load_price_observations()
explicitly filters Source='HARTI' to freeze the training frame -- see
features-consumer audit in the task report), so AsOfUtc is presently INERT
for the ML pipeline. It is still computed correctly here because (a) it is a
required, NOT NULL column (PriceObservation.Create rejects default(DateTime))
and (b) a future phase that widens load_price_observations() to include DEC
must not inherit a silently-wrong vintage.
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

# Dambulla Markets.Id is resolved AT RUNTIME by this code, never a hardcoded
# GUID (same rule as harti/loader.py's _dambulla_market_id /
# MarketPriceIngestionService.cs's DambullaMarketCode).
DAMBULLA_MARKET_CODE = "MKT00000001"


# --------------------------------------------------------------------------
# Shared WHERE-clause fragment: which MarketPrices rows are eligible to
# mirror. Used identically by the COUNT (dry-run / reporting) and INSERT
# statements below so the two can never drift apart.
# --------------------------------------------------------------------------
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
    """Read-only counts: what would ``mirror_dec_to_price_observations`` do?

    Never writes. Safe to run against the live DB at any time (used by the
    hub's pre-backfill dry-run report and by mirror_dec_prices.py --dry-run).

    Returns:
        {
          "market_id": str,
          "eligible_total": int,       # DEC rows passing the fail-closed
                                        # gate (positive price, resolved
                                        # CropId), before dedup against
                                        # PriceObservations.
          "already_mirrored": int,     # of those, how many already exist in
                                        # PriceObservations (id-keyed match).
          "would_insert": int,         # eligible_total - already_mirrored.
          "skipped_non_positive": int, # DEC rows rejected: MinPrice<=0 or
                                        # MaxPrice<=0.
          "skipped_null_crop": int,    # DEC rows with CropId IS NULL.
          "skipped_null_extid": int,   # DEC rows with ExternalProductId IS
                                        # NULL (defense-in-depth; 0 live
                                        # today -- see module docstring
                                        # "NULL ExternalProductId GUARD").
          "min_observed_date": str | None,
          "max_observed_date": str | None,
          "distinct_crops": int,
        }
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

    Single set-based ``INSERT ... SELECT ... WHERE NOT EXISTS`` (idempotent,
    insert-only -- see module docstring for the full rationale). Re-running
    with no new MarketPrices rows returns inserted=0.

    Args:
        engine:      SQLAlchemy engine; created from config if None.
        since_date:  Only consider MarketPrices rows with PriceDate STRICTLY
                     AFTER this date (daily-delta optimisation; the anti-join
                     is already correct/idempotent without this, so it exists
                     purely to keep the daily pass from re-scanning the whole
                     table as it grows -- correctness does not depend on it).
                     None (default) = full backfill, no lower bound.
        dry_run:     If True, resolve + count but issue no writes (identical
                     numbers to dry_run_report(), returned in the same shape
                     plus inserted=0).

    Returns:
        dict with the same keys as dry_run_report() plus:
          "inserted": int   -- actual rows written this call (0 if dry_run).
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
