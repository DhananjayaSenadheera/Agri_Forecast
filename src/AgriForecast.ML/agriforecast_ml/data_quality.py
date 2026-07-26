"""Data-quality checks for the ingestion paths.

Fail-loud throughout: a violation is either raised (a non-positive price at write time,
cross-source duplicates reaching the feature build) or returned as a structured report
entry with a severity someone can act on. Nothing is silently dropped or silently fixed.

validate_price_row - the shared, source-agnostic pre-write guard.
  A price of 0 or below is REJECTED, not written: some feeds use zero to mean 'no trade
  that day', which is explicit missingness and must not be stored as a real price.
  min_price > max_price is QUARANTINED, never silently swapped. The HARTI parser already
  fixes its own typo case upstream, so a min>max arriving here would be a structurally
  different problem, and swapping would launder a real bug into a plausible number.
  Quarantine means writing the row with IsUnitConfirmed=0 - reusing the existing unit
  quarantine column and its 'features read WHERE IsUnitConfirmed = 1' contract - plus a
  WARNING. The row is kept, because an ambiguous price is still evidence of a trade and
  dropping it would be indistinguishable from a market closure.

gap_report - runs of missing days per series.
  Tiers: 1-2 missing days INFO, 3-7 WARNING, 8+ ERROR. An ERROR is a report entry needing
  manual acknowledgement, never an exception that kills an otherwise-good ingestion run.
  Calibrated on the real corpus, where the 8+ tier only caught genuine publication
  interruptions (the 2020 COVID lockdown and the 2021 curfew waves). A missing day that
  falls on a Poya day is excluded from the run entirely - it is expected, not missing.
  Sunday is deliberately NOT suppressed; see poya.py for the corpus evidence.

flag_price_outliers - rolling 90-day IQR hold per (CropId, MarketId).
  Backward-looking only: the window for a candidate at date T uses observations strictly
  before T, so a candidate never influences its own reference statistics.
  The fence is median + 4*IQR, deliberately looser than the textbook 1.5x, because real
  festival and shortage spikes are market signal and this check is for parsing and unit
  errors. A series with too few points in the window is skipped, not flagged. Flagged
  rows are HELD (IsUnitConfirmed=0), never deleted, and clear_outlier_hold() unquarantines
  exactly one row at a time.

assert_no_source_duplicates - guards the (CropId, ObservedDate, MarketId) triple.
  HARTI, the DEC mirror and CBSL legitimately observe some of the same markets, so this
  is not a naive COUNT(DISTINCT Source) > 1 check: a triple is allowed only when its
  source set is a subset of the adjudicated set for THAT market. The same pair at a
  market it was not adjudicated for is exactly the mis-resolved-market bug this catches.

assert_vintage_sane / assert_effective_not_future - pure point-in-time guards with no
table dependency, so the discipline is pinned by tests before the callers exist.
"""
from __future__ import annotations

import logging
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone
from typing import Sequence

import sqlalchemy as sa

from .db import get_engine
from .poya import is_poya

logger = logging.getLogger(__name__)



@dataclass
class RowValidationResult:
    """Outcome of validate_price_row() for one candidate row.

    accepted: write the row normally; this validator does not touch the unit flag.
    reject:   do not write the row at all (e.g. a non-positive price). Excludes hold.
    hold:     write the row but force IsUnitConfirmed=0 (e.g. min>max). Excludes reject.
    reason:   short machine-readable code, e.g. 'non_positive_price' or 'ok'.
    message:  human-readable detail for the WARNING log line.
    """
    accepted: bool
    reject: bool = False
    hold: bool = False
    reason: str = "ok"
    message: str = ""


def validate_price_row(
    *,
    min_price: float,
    max_price: float,
    source: str,
    crop_label: str,
    observed_date: date,
    market_name: "str | None" = None,
) -> RowValidationResult:
    """Source-agnostic pre-write guard: reject a non-positive price, quarantine min>max.

    A pure function with no DB access, so any source writer can call it before an upsert.
    Callers log the returned message themselves at the point of the reject or hold, so a
    caller processing a large backlog can log once per batch rather than flooding.
    """
    tag = f"[{observed_date.isoformat()}] {market_name or '?'}/{crop_label} ({source})"

    if min_price <= 0 or max_price <= 0:
        return RowValidationResult(
            accepted=False,
            reject=True,
            reason="non_positive_price",
            message=(
                f"{tag}: non-positive price (min={min_price!r}, "
                f"max={max_price!r}) -- rejecting row, not writing a "
                f"fabricated zero price (market-closed/no-trade should be "
                f"an absent row, not a stored 0)."
            ),
        )

    if min_price > max_price:
        return RowValidationResult(
            accepted=True,
            hold=True,
            reason="min_greater_than_max",
            message=(
                f"{tag}: MinPrice ({min_price}) > MaxPrice ({max_price}) -- "
                f"ambiguous data, HOLDING (IsUnitConfirmed=0 quarantine), "
                f"not silently swapping. See data_quality.py Section 2 for "
                f"rationale."
            ),
        )

    return RowValidationResult(accepted=True, reason="ok")



GAP_TIER_INFO_MAX = 2      # 1-2 missing days -> INFO
GAP_TIER_WARNING_MAX = 7   # 3-7 missing days -> WARNING
# 8+ missing days -> ERROR (requires manual ack)

MIN_OBSERVATIONS_FOR_GAP_SCAN = 5  # fewer than this = cold-start, not a gap series


def _gap_tier(missing_days: int) -> str:
    if missing_days <= GAP_TIER_INFO_MAX:
        return "INFO"
    if missing_days <= GAP_TIER_WARNING_MAX:
        return "WARNING"
    return "ERROR"


@dataclass
class GapEntry:
    crop_label: str
    market_name: str
    source: str
    gap_start: str      # last observed date before the gap (ISO)
    gap_end: str         # next observed date after the gap (ISO)
    missing_days: int    # count of missing calendar days strictly between
    tier: str            # INFO / WARNING / ERROR
    poya_suppressed_days: int = 0  # how many of the missing days were Poya (not counted in missing_days)


def gap_report(
    engine: "sa.engine.Engine | None" = None,
    *,
    source: str = "HARTI",
) -> dict:
    """Scan PriceObservations for runs of missing ObservedDates per series.

    A gap is the calendar days strictly between two consecutive observed dates, minus Poya
    days: a Poya day inside an otherwise 1-day gap makes the gap vanish entirely rather than
    just lowering its tier. Series with fewer than MIN_OBSERVATIONS_FOR_GAP_SCAN observations
    are skipped and counted under insufficient_history.

    Never raises for gap findings themselves - an 8+ day ERROR gap is a report entry that
    needs manual acknowledgement. Returns the entries, the per-tier counts, the
    insufficient_history list and series_scanned. Each entry is also logged at its own tier.
    """
    eng = engine if engine is not None else get_engine()

    with eng.connect() as conn:
        rows = conn.execute(sa.text(
            """SELECT ExternalCommodityName, MarketId, m.Name AS MarketName,
                      Source, ObservedDate
               FROM PriceObservations po
               JOIN Markets m ON m.Id = po.MarketId
               WHERE po.Source = :source
               ORDER BY ExternalCommodityName, MarketId, ObservedDate"""
        ), {"source": source}).fetchall()

    from collections import defaultdict
    by_series: dict[tuple[str, str, str], list[date]] = defaultdict(list)
    market_name_by_id: dict[str, str] = {}
    for ext_name, market_id, market_name, row_source, observed_date in rows:
        if isinstance(observed_date, str):
            observed_date = date.fromisoformat(observed_date)
        elif hasattr(observed_date, "date") and not isinstance(observed_date, date):
            observed_date = observed_date.date()
        key = (ext_name, str(market_id), row_source)
        by_series[key].append(observed_date)
        market_name_by_id[str(market_id)] = market_name

    entries: list[dict] = []
    insufficient_history: list[dict] = []
    n_info = n_warning = n_error = 0

    for (crop_label, market_id, row_source), dates in sorted(by_series.items()):
        dates = sorted(dates)
        if len(dates) < MIN_OBSERVATIONS_FOR_GAP_SCAN:
            insufficient_history.append({
                "crop_label": crop_label,
                "market_name": market_name_by_id.get(market_id, market_id),
                "source": row_source,
                "n_observations": len(dates),
            })
            continue

        for i in range(1, len(dates)):
            start, end = dates[i - 1], dates[i]
            calendar_gap = (end - start).days - 1  # missing calendar days strictly between
            if calendar_gap <= 0:
                continue
            poya_suppressed = sum(
                1 for n in range(1, calendar_gap + 1)
                if is_poya(start + timedelta(days=n))
            )
            missing_days = calendar_gap - poya_suppressed
            if missing_days <= 0:
                continue  # entire gap explained by Poya closures

            tier = _gap_tier(missing_days)
            entry = GapEntry(
                crop_label=crop_label,
                market_name=market_name_by_id.get(market_id, market_id),
                source=row_source,
                gap_start=start.isoformat(),
                gap_end=end.isoformat(),
                missing_days=missing_days,
                tier=tier,
                poya_suppressed_days=poya_suppressed,
            )
            entries.append(entry.__dict__)

            log_msg = (
                "gap_report: %s/%s (%s) gap %s -> %s: %d missing day(s) "
                "(tier=%s, %d Poya-suppressed)"
            )
            log_args = (
                entry.crop_label, entry.market_name, entry.source,
                entry.gap_start, entry.gap_end, entry.missing_days,
                entry.tier, entry.poya_suppressed_days,
            )
            if tier == "ERROR":
                n_error += 1
                logger.error(log_msg, *log_args)
            elif tier == "WARNING":
                n_warning += 1
                logger.warning(log_msg, *log_args)
            else:
                n_info += 1
                logger.info(log_msg, *log_args)

    logger.info(
        "gap_report: %d series scanned, %d gap entries (info=%d warning=%d "
        "error=%d), %d series skipped for insufficient history",
        len(by_series), len(entries), n_info, n_warning, n_error,
        len(insufficient_history),
    )

    return {
        "entries": sorted(entries, key=lambda e: (e["crop_label"], e["market_name"], e["gap_start"])),
        "n_info": n_info,
        "n_warning": n_warning,
        "n_error": n_error,
        "insufficient_history": insufficient_history,
        "series_scanned": len(by_series),
    }



OUTLIER_ROLLING_WINDOW_DAYS = 90
OUTLIER_IQR_MULTIPLIER = 4.0
MIN_OBSERVATIONS_FOR_OUTLIER_CHECK = 20  # cold-start floor within the window


def _iqr_median(values: Sequence[float]) -> "tuple[float, float] | None":
    """Return (median, iqr) for a sequence, or None when there are too few points.

    Plain Python using the exclusive quartile method - coarse enough for a 4x-IQR fence.
    """
    n = len(values)
    if n < 2:
        return None
    s = sorted(values)

    def _median(seq: Sequence[float]) -> float:
        m = len(seq)
        mid = m // 2
        if m % 2 == 1:
            return seq[mid]
        return (seq[mid - 1] + seq[mid]) / 2.0

    med = _median(s)
    lower_half = s[: n // 2]
    upper_half = s[(n + 1) // 2:]
    if not lower_half or not upper_half:
        return None
    q1 = _median(lower_half)
    q3 = _median(upper_half)
    return med, (q3 - q1)


def flag_price_outliers(
    engine: "sa.engine.Engine | None" = None,
    *,
    source: str = "HARTI",
    dry_run: bool = False,
) -> dict:
    """Rolling 90-day IQR outlier hold per (CropId, MarketId).

    Leakage discipline: for a candidate at ObservedDate T the reference window is every
    observation of the same (CropId, MarketId, Source) with T-90 <= ObservedDate < T, strictly
    before T, so the candidate never enters its own statistics and nothing at or after T is
    read. Computed in Python per series rather than as a SQL window so the boundary is exact
    and easy to pin in a test.

    Rows with CropId NULL are skipped: an outlier fence needs a stable crop identity, and an
    unresolved crop is already reported by heal_price_observation_crops().

    Args:
        engine:   SQLAlchemy engine; created from config if None.
        source:   Source to scan (default 'HARTI').
        dry_run:  Compute and report flags without writing IsUnitConfirmed=0.

    Returns the flagged rows plus series_checked, series_skipped_cold_start and n_flagged.
    """
    eng = engine if engine is not None else get_engine()

    with eng.connect() as conn:
        rows = conn.execute(sa.text(
            """SELECT Id, CropId, MarketId, ExternalCommodityName,
                      ObservedDate, MinPrice, MaxPrice
               FROM PriceObservations
               WHERE Source = :source
                 AND CropId IS NOT NULL
                 AND MinPrice IS NOT NULL
                 AND MaxPrice IS NOT NULL
               ORDER BY CropId, MarketId, ObservedDate"""
        ), {"source": source}).fetchall()

    from collections import defaultdict
    by_series: dict[tuple[str, str], list[dict]] = defaultdict(list)
    for row_id, crop_id, market_id, ext_name, observed_date, min_price, max_price in rows:
        if isinstance(observed_date, str):
            observed_date = date.fromisoformat(observed_date)
        elif hasattr(observed_date, "date") and not isinstance(observed_date, date):
            observed_date = observed_date.date()
        key = (str(crop_id), str(market_id))
        by_series[key].append({
            "id": row_id,
            "external_commodity_name": ext_name,
            "observed_date": observed_date,
            "midpoint": (float(min_price) + float(max_price)) / 2.0,
        })

    flagged: list[dict] = []
    series_skipped_cold_start = 0

    for (crop_id, market_id), obs in by_series.items():
        obs.sort(key=lambda o: o["observed_date"])
        if len(obs) < MIN_OBSERVATIONS_FOR_OUTLIER_CHECK:
            series_skipped_cold_start += 1
            continue

        for i, candidate in enumerate(obs):
            t = candidate["observed_date"]
            window_start = t - timedelta(days=OUTLIER_ROLLING_WINDOW_DAYS)
            # Strictly BEFORE t - the leakage guard. obs is sorted, so this prefix scan is
            # fine at this corpus scale.
            reference = [
                o["midpoint"] for o in obs[:i]
                if window_start <= o["observed_date"] < t
            ]
            if len(reference) < MIN_OBSERVATIONS_FOR_OUTLIER_CHECK:
                continue  # cold-start within the window itself

            stats = _iqr_median(reference)
            if stats is None:
                continue
            median, iqr = stats
            if iqr <= 0:
                continue  # degenerate (flat) window -- nothing to compare against
            threshold = median + OUTLIER_IQR_MULTIPLIER * iqr

            if candidate["midpoint"] > threshold:
                flagged.append({
                    "id": candidate["id"],
                    "crop_id": crop_id,
                    "market_id": market_id,
                    "external_commodity_name": candidate["external_commodity_name"],
                    "observed_date": t.isoformat(),
                    "midpoint": candidate["midpoint"],
                    "rolling_median": median,
                    "rolling_iqr": iqr,
                    "threshold": threshold,
                })
                logger.warning(
                    "flag_price_outliers: %s (crop=%s market=%s) midpoint=%.2f "
                    "exceeds rolling-90d threshold=%.2f (median=%.2f, iqr=%.2f, "
                    "%dx) -- %s",
                    t.isoformat(), crop_id, market_id, candidate["midpoint"],
                    threshold, median, iqr, OUTLIER_IQR_MULTIPLIER,
                    "HOLDING (IsUnitConfirmed=0)" if not dry_run else "DRY RUN, not writing",
                )

    if not dry_run and flagged:
        with eng.begin() as conn:
            for f in flagged:
                conn.execute(sa.text(
                    """UPDATE PriceObservations
                       SET IsUnitConfirmed = 0
                       WHERE Id = :id"""
                ), {"id": f["id"]})

    logger.info(
        "flag_price_outliers: %d series checked, %d skipped (cold-start), "
        "%d rows flagged%s",
        len(by_series) - series_skipped_cold_start, series_skipped_cold_start,
        len(flagged), " (DRY RUN)" if dry_run else "",
    )

    return {
        "flagged": flagged,
        "series_checked": len(by_series) - series_skipped_cold_start,
        "series_skipped_cold_start": series_skipped_cold_start,
        "n_flagged": len(flagged),
    }


def clear_outlier_hold(
    row_id,
    *,
    engine: "sa.engine.Engine | None" = None,
) -> bool:
    """Admin helper: set IsUnitConfirmed=1 for exactly one row, by Id.

    Deliberately single-row and parameterized - there is no bulk unquarantine, so clearing a
    hold is always a specific, auditable decision. Returns True if a row was updated.
    """
    eng = engine if engine is not None else get_engine()
    with eng.begin() as conn:
        result = conn.execute(sa.text(
            """UPDATE PriceObservations
               SET IsUnitConfirmed = 1
               WHERE Id = :id"""
        ), {"id": str(row_id)})
        updated = result.rowcount > 0
    if updated:
        logger.info("clear_outlier_hold: cleared quarantine on row %s", row_id)
    else:
        logger.warning("clear_outlier_hold: no row found for id %s", row_id)
    return updated



# The one named pair of sources that may legitimately coexist at the same triple: HARTI's
# own Dambulla bulletin column plus the additive DEC mirror. Kept for message text; the
# operative structure is the per-market map below.
_ALLOWED_COEXISTING_SOURCES = frozenset({"HARTI", "DAMBULLA_DEC"})

# Adjudicated overlaps are scoped PER MARKET: an allowed source pair appearing at a market
# it was not adjudicated for is not the by-design overlap, it is the double-write bug this
# check exists to catch. CBSL deliberately observes markets other sources already cover -
# that is its cross-validation value - so each market's entry is the FULL set of sources
# allowed there, and a triple passes when its source set is a SUBSET of that.
#
# Markets are resolved BY CODE at call time, never a hardcoded GUID, and fail closed: a
# missing Markets row raises rather than silently widening or narrowing the allowance.
_DAMBULLA_MARKET_CODE = "MKT00000001"

_ADJUDICATED_OVERLAPS: dict[str, frozenset] = {
    _DAMBULLA_MARKET_CODE: frozenset({"HARTI", "DAMBULLA_DEC", "CBSL"}),
    "MKT00000004": frozenset({"HARTI", "CBSL"}),  # Pettah (HARTI wholesale)
    "MKT00000005": frozenset({"HARTI", "CBSL"}),  # Narahenpita (HARTI retail)
}


def assert_no_source_duplicates(engine: "sa.engine.Engine | None" = None) -> int:
    """Assert that no (CropId, ObservedDate, MarketId) triple carries an unadjudicated source
    combination.

    Two-step SQL: find the candidate triples with COUNT(DISTINCT Source) > 1, then pull their
    actual source values and flag, in Python, any whose source set is not a subset of that
    MARKET's adjudicated set. A genuine third source, an unexpected pair, or an allowed pair
    appearing at a market it was not adjudicated for all still raise.

    Scoped to CropId IS NOT NULL: an unresolved crop has no stable identity to dedup on.

    Raises RuntimeError if an adjudicated Markets row cannot be resolved (fail-closed), and
    AssertionError listing the offending triples, at most 20 shown. Returns the number of
    distinct triples examined.
    """
    from .canonical import resolve_market_id_by_code

    eng = engine if engine is not None else get_engine()
    # Adjudicated-market ids resolved BY CODE, fail-closed (a missing row raises).
    allowed_by_market_id: dict[str, frozenset] = {
        str(resolve_market_id_by_code(eng, market_code=code)).upper(): sources
        for code, sources in _ADJUDICATED_OVERLAPS.items()
    }

    with eng.connect() as conn:
        candidate_rows = conn.execute(sa.text(
            """SELECT po.CropId, po.ObservedDate, po.MarketId, po.Source
               FROM PriceObservations po
               JOIN (
                   SELECT CropId, ObservedDate, MarketId
                   FROM PriceObservations
                   WHERE CropId IS NOT NULL
                   GROUP BY CropId, ObservedDate, MarketId
                   HAVING COUNT(DISTINCT Source) > 1
               ) dk ON dk.CropId = po.CropId
                   AND dk.ObservedDate = po.ObservedDate
                   AND dk.MarketId = po.MarketId"""
        )).fetchall()

        total = conn.execute(sa.text(
            """SELECT COUNT(DISTINCT CONCAT(
                        CONVERT(varchar(36), CropId), '|',
                        CONVERT(varchar(10), ObservedDate), '|',
                        CONVERT(varchar(36), MarketId)))
               FROM PriceObservations
               WHERE CropId IS NOT NULL"""
        )).scalar() or 0

    from collections import defaultdict
    by_triple: dict[tuple, set] = defaultdict(set)
    for crop_id, observed_date, market_id, source in candidate_rows:
        key = (str(crop_id), str(observed_date), str(market_id))
        by_triple[key].add(source)

    def _is_allowed(key: tuple, sources: set) -> bool:
        # Allowed only when this market has an adjudicated set and the actual source set is a
        # subset of it. Any source combination at a non-adjudicated market is a violation.
        _crop_id, _obs_date, market_id = key
        allowed = allowed_by_market_id.get(market_id.upper())
        return allowed is not None and sources <= allowed

    violations = {
        key: sources for key, sources in by_triple.items()
        if not _is_allowed(key, sources)
    }
    n_allowed = len(by_triple) - len(violations)

    if violations:
        dup_lines = "\n".join(
            f"  CropId={crop_id} ObservedDate={obs_date} MarketId={market_id} "
            f"sources={sorted(sources)}"
            for (crop_id, obs_date, market_id), sources in list(violations.items())[:20]
        )
        raise AssertionError(
            f"SOURCE DUPLICATION: {len(violations)} (CropId, ObservedDate, "
            f"MarketId) triples exist from a Source combination/location "
            f"outside the adjudicated per-market overlaps "
            f"({ {c: sorted(s) for c, s in _ADJUDICATED_OVERLAPS.items()} }):"
            f"\n{dup_lines}"
            + (f"\n  ... and {len(violations) - 20} more" if len(violations) > 20 else "")
        )

    logger.info(
        "assert_no_source_duplicates: PASSED -- %d (CropId, ObservedDate, "
        "MarketId) triples examined, 0 unexpected cross-source duplicates "
        "(%d legitimate adjudicated overlaps allowed by design)",
        total, n_allowed,
    )
    return total



def assert_vintage_sane(
    publication_date: date,
    reference_period_start: date,
    *,
    now: "date | None" = None,
) -> None:
    """Reject an impossible publication vintage.

    A vintage published in the future, or published before the reference period it describes
    even started, cannot be real.

    Args:
        publication_date:       the vintage/publication date to validate.
        reference_period_start: the start of the period the publication describes.
        now:                    injected 'today' for testability; real callers omit it.

    Raises ValueError if publication_date is after now, or before reference_period_start.
    """
    today = now if now is not None else date.today()
    if publication_date > today:
        raise ValueError(
            f"assert_vintage_sane: publication_date {publication_date} is "
            f"in the future relative to now={today} -- impossible vintage."
        )
    if publication_date < reference_period_start:
        raise ValueError(
            f"assert_vintage_sane: publication_date {publication_date} is "
            f"before its own reference_period_start "
            f"{reference_period_start} -- cannot publish a statistic about "
            f"a period that has not started."
        )


def assert_effective_not_future(
    effective_from: date,
    *,
    now: "date | None" = None,
) -> None:
    """Reject an effective-from date that lies in the future.

    A point-in-time feature join must never treat a not-yet-effective policy or budget change
    as already in force. now is injected for testability; real callers omit it.

    Raises ValueError if effective_from is after now.
    """
    today = now if now is not None else date.today()
    if effective_from > today:
        raise ValueError(
            f"assert_effective_not_future: effective_from {effective_from} "
            f"is in the future relative to now={today} -- a not-yet-"
            f"effective date must not be treated as already in force."
        )
