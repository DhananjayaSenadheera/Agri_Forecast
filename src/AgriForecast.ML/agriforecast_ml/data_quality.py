"""Data-quality checks (R1.1 P1 Step 5, ClickUp 86cahef64, PRD Section 2.5).

Owner: data engineer. Fail-loud discipline throughout: a violation is either
raised (hard-stop conditions — negative price at write time, cross-source
duplicates reaching the feature build) or returned as a structured report
entry with an explicit severity a Worker/human can act on (gap tiers,
outlier holds) — never silently dropped or silently fixed.

This module is intentionally source-agnostic where the task calls for it
(``validate_price_row`` — HARTI today, CBSL/DEC-in-Python whenever they
land) and clearly scoped to the current single-source reality where it
isn't (``gap_report``/``flag_price_outliers`` read ``PriceObservations``,
which today only HARTI writes; ``assert_no_source_duplicates`` is written as
real multi-source SQL now even though it cannot yet find anything to flag).

============================================================================
1. SHARED ROW VALIDATOR — validate_price_row()
============================================================================
Applied to a parsed row BEFORE it is written, by any source writer (see
harti/loader.py::upsert_harti_price_observations, the call site wired in
this task). Two independent guards:

  (a) Non-positive price -> REJECT the row + WARN. A live query against the
      real corpus (MarketPrices, all sources) found this is a genuine,
      non-hypothetical problem: 196 DAMBULLA_DEC rows (Big Onion, Chaw-
      Chaw, Red Cabbage, ... on 2025-05-05, the DEC launch date) carry
      MinPrice=MaxPrice=0.00. Zero is being used there to mean "no trade /
      market didn't report this crop that day" — which is exactly the kind
      of explicit-missingness signal this project's disciplines say must be
      preserved, NOT encoded as a fabricated zero price sitting in a price
      column. A price of 0 or below is therefore rejected at the row-
      validator level (not written to PriceObservations at all) rather than
      accepted as "the price was zero" — see the design-decision note in
      this module's tail comment for the full rationale and the
      alternative considered.

  (b) min_price > max_price -> QUARANTINE, do not silently swap. See the
      dedicated section below (2) for why this differs from the existing
      HARTI parser behaviour.

============================================================================
2. MIN > MAX HANDLING — DESIGN DECISION (pinned in
   TestValidatePriceRow::test_min_greater_than_max_is_quarantined_not_swapped)
============================================================================
IMPORTANT PRE-EXISTING BEHAVIOUR: harti/parser.py::_parse_price_cell()
ALREADY silently swaps lo/hi when lo > hi ("Guard: occasionally lo > hi due
to typo; swap silently") before a ParsedPrice ever exists. A live corpus
query confirms this swap essentially never needs to fire in practice: 0 of
14,576 HARTI MarketPrices rows and 0 of 33,022 DAMBULLA_DEC rows have
MinPrice > MaxPrice today. So for the HARTI source specifically, by the
time a row reaches this validator, min <= max is already guaranteed by the
parser — this validator's min>max branch is currently a defensive backstop
that is not observed to fire on the real HARTI corpus, not a fix for an
observed problem.

This validator deliberately does NOT swap. Rationale for diverging from the
parser's existing swap-silently behaviour:
  - The parser's swap is scoped to a SINGLE cell of a SINGLE, well-understood
    source (a "220.00 - 200.00"-shaped typo in one bulletin), where the
    author judged a swap is almost certainly what was meant to be typed.
  - This validator is the SHARED, source-agnostic gate the task brief
    explicitly asks future sources (CBSL, DEC) to reuse. A future source's
    min>max could mean something structurally different (e.g. two columns
    genuinely transposed by a parser bug, not a data-entry typo) — silently
    swapping there would launder a real bug into a plausible-looking wrong
    number that nobody would ever notice again. A held/quarantined row is
    loud and reversible (someone can inspect and fix it later); a silent
    swap is neither.
  - This project's stated disposition (canonical.py's unit-quarantine
    contract, IsUnitConfirmed defaulting closed) is: when data is
    ambiguous, fail closed and make a human/future-process resolve it,
    never guess. min>max is exactly this kind of ambiguity.
  Net effect for HARTI today: because the parser already resolves the
  ambiguity upstream, this branch is inert on the current corpus (0 real
  hits) — it exists for the day CBSL/DEC or a future HARTI layout change
  produces a genuine min>max row, at which point "hold + WARN" is the
  correct fail-closed behaviour rather than a silent, unaudited swap.

  CHOSEN MECHANISM: "hold" = write the row with IsUnitConfirmed=0 (reusing
  the existing unit-quarantine column and its existing feature-layer
  exclusion contract — WHERE IsUnitConfirmed = 1 — rather than inventing a
  second quarantine flag) + log WARNING with the offending numbers. The row
  is NOT skipped/dropped: an ambiguous price is still evidence something
  happened that day (the crop was probably traded), and dropping it would
  turn an ambiguous-but-real observation into a silent gap indistinguishable
  from a market closure. Quarantining (write it, exclude it from features
  until confirmed) preserves both the audit trail and the "don't leak bad
  data downstream" requirement.

============================================================================
3. gap_report() — tiers, Poya suppression, corpus-calibrated
============================================================================
Tiers (per task brief): 1-2 consecutive missing days -> INFO, 3-7 ->
WARNING, 8+ -> ERROR (a structured report entry requiring manual
acknowledgement, NOT a raised exception — an 8+ day gap must not kill an
otherwise-successful ingestion run; see run_all_data_quality_checks()).

Calibration against the real corpus (MarketPrices, Source='HARTI', the
proxy for PriceObservations coverage until the latter is backfilled — see
this module's live-verification notes in the task report): a per-crop gap
scan (2015-06-22 to 2025-06-30) found 508 gap-events; 416 were 1-2 days
(normal single-day closures, weekends), 86 were 3-7 days (extended
closures — plausible short public-holiday clusters), and 6 were 8+ days,
matching real, known disruptions:
  - 2017-01-31 -> 2017-03-01 (28 missing days)
  - 2020-03-13 -> 2020-06-15 (93 missing days) -- Sri Lanka's COVID-19
    lockdown period; HARTI simply did not publish bulletins.
  - Four more 8-17-day gaps clustered in 2021-04 to 2021-06 -- the 2021
    COVID "third wave" curfew period.
  So the ERROR tier's real-world hit rate is exactly what an ingestion
  Worker would want surfaced for manual ack (genuine multi-week/multi-month
  publication interruptions), not noise.

Keppetipola thin coverage (per harti_multimarket_audit.md Section 8.6,
confirmed live): the Keppetipola market column does not exist in the
bulletin at all before 2017-03-08 (pinned exactly: absent 2017-03-07,
present 2017-03-08), and even after that date only carries data for 2 of
the 6 target crops (Beans, Capsicum) with any regularity — the other 4
routinely show "-" (located column, no trade that day) rather than a
missing column. gap_report() treats "column absent" (pre-2017-03-08) as
what it is: no observations exist for that (crop, market) key at all in
that window, which this function reports via the ``insufficient_history``
counter (see cold-start note in flag_price_outliers) rather than a
misleading wall of daily "gap" entries for a market/crop pair that simply
didn't exist yet.

Poya suppression: a missing ObservedDate that falls on a Poya day (see
poya.py) is excluded from the gap run-length entirely (it does not count
towards ANY tier, including INFO) — it is expected, not missing. See
poya.py's module docstring for why Sunday is NOT also suppressed (live
corpus evidence: HARTI does publish on a meaningful fraction of Sundays).

============================================================================
4. Outlier hold — flag_price_outliers() / clear_outlier_hold()
============================================================================
Rolling 90-calendar-day IQR per (CropId, MarketId), backward-looking only
(leakage discipline: the window for a candidate observation at date T uses
only observations with ObservedDate < T -- strictly BEFORE, so the
candidate itself never influences its own reference statistics; pinned in
TestNoLeakage).

Threshold: midpoint ((MinPrice+MaxPrice)/2) > median + 4*IQR is flagged.
4x is deliberately conservative (loose) rather than the textbook 1.5x
"mild outlier" fence: HARTI price data legitimately swings hard around
festivals/shortages (e.g. drought-driven vegetable price spikes are real
market signal, not data errors), and this check's job is to catch parsing/
unit errors (a stray extra digit, a Rs/kg row that's actually Rs/50kg),
not to flag every real demand spike as suspicious. A tighter fence would
generate so many false positives on legitimate volatility that the
WARNING would stop being trustworthy signal.

Cold-start: a series with fewer than MIN_OBSERVATIONS_FOR_OUTLIER_CHECK
(20) observations in the trailing 90-day window is skipped, not flagged --
matches this project's cold-start discipline (thin history is a routing
signal for agri-ml-engineer's fallback logic, not something to guess an
IQR fence for on 3 data points). This also protects newly-onboarded
markets/crops (e.g. Keppetipola's 4 thin-coverage crops) from spurious
flags the moment they get their first few observations.

Mechanism: HELD, never deleted -- sets IsUnitConfirmed=0 on the flagged
row (same quarantine column/contract as the min>max hold above; a
quarantined row is already excluded from any WHERE IsUnitConfirmed=1
feature read). clear_outlier_hold() is the single-row, parameterized,
admin unquarantine path -- deliberately no bulk "unquarantine everything"
call, so clearing a hold is always a specific, auditable, one-row decision.

============================================================================
5. assert_no_source_duplicates()
============================================================================
Real SQL (GROUP BY (CropId, ObservedDate, MarketId) HAVING
COUNT(DISTINCT Source) > 1, CropId IS NOT NULL) against PriceObservations.
Today only HARTI writes PriceObservations, so this passes trivially (there
is only ever one Source value present) -- it exists now so the day CBSL/DEC
land in Python (P3+) and could theoretically double-write the same
(crop, date, market) triple, the guard is already live and already tested,
not bolted on retroactively after a real double-count incident.

R2 Step 2 (DEC -> PriceObservations mirror, ClickUp 86cajtx2k) update: DEC/
HARTI now legitimately COEXIST at the SAME (CropId, ObservedDate, MarketId)
triple at Dambulla -- HARTI already parses a Dambulla column from its own
bulletins (Step 6 widened it to 10 markets including Dambulla), and the
mirror additively writes Source='DAMBULLA_DEC' rows at the same market. This
is BY DESIGN (the whole point of the refined-layout mirror -- PriceObservations
becomes the single table holding every source), not a double-count bug, so
the naive ``COUNT(DISTINCT Source) > 1`` check would now fire ~8,096 false
positives (live-measured overlap at Dambulla alone) and stop meaning anything.
_ALLOWED_COEXISTING_SOURCES = {"HARTI", "DAMBULLA_DEC"} is the one, narrow,
named carve-out: a triple is flagged ONLY if its actual source SET is
anything OTHER than exactly this pair (a genuine 3rd source appearing, or an
unexpected two-source combination that isn't this specific, adjudicated
overlap) -- so the check still catches every OTHER double-write scenario it
was built to catch; it does not weaken the general N-source guard, it only
recognises the one pairing this project has explicitly decided is correct.

SF2 (reviewer should-fix): the carve-out is additionally scoped to the
DAMBULLA MARKET ONLY (resolved by MarketCode, fail-closed via
canonical.resolve_market_id_by_code -- see _DAMBULLA_MARKET_CODE below). DEC
never legitimately writes to any market but Dambulla, so a HARTI+DAMBULLA_DEC
pair appearing anywhere else is NOT the adjudicated overlap -- it is exactly
a mis-resolved-market bug (e.g. a future mirror change that accidentally
targets Pettah) and must still raise.

============================================================================
6. Macro point-in-time guard (P3 stub, generic + reusable now)
============================================================================
assert_vintage_sane() / assert_effective_not_future() are pure functions
with no P3 table dependency -- the macro-indicator/BudgetEvents tables this
guard will eventually wire into (PublicationDate, EffectiveFrom columns)
are P3 scope and do not exist yet in this schema. Implementing the two
functions now (rather than waiting for the tables) means the point-in-time
discipline is pinned in a test today, and P3 only has to call them, not
design them under time pressure.
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


# ============================================================================
# 1. Shared row validator (source-agnostic)
# ============================================================================

@dataclass
class RowValidationResult:
    """Outcome of validate_price_row() for one candidate row.

    accepted:       True if the row should be written normally
                     (IsUnitConfirmed left to the caller's own unit
                     contract -- this validator does not touch the unit
                     flag for an accepted row).
    reject:         True if the row must NOT be written at all (e.g.
                     non-positive price). Mutually exclusive with `hold`.
    hold:           True if the row should be written but QUARANTINED
                     (IsUnitConfirmed=0 forced regardless of the source's
                     own unit-confirmation status) -- e.g. min>max.
                     Mutually exclusive with `reject`.
    reason:         Short machine-readable code, e.g.
                     "non_positive_price", "min_greater_than_max", "ok".
    message:        Human-readable detail for the WARNING log line.
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
    """Source-agnostic pre-write guard: negative/zero price rejection +
    min>max quarantine. See module docstring Sections 1-2 for full
    rationale. Pure function (no DB access) so any source writer (HARTI
    today; CBSL/DEC later) can call it uniformly before an upsert.

    Callers MUST log the returned WARNING via `message` themselves at the
    point of the actual reject/hold decision (mirrors this codebase's
    existing pattern of logging at the call site with full row context,
    e.g. harti/loader.py's per-row WARN calls) -- this function returns the
    text but does not log directly, so a caller processing many rows can
    choose to log once per PDF/batch rather than flooding on a large
    backlog, exactly like heal_price_observation_crops() does for
    unresolved labels.
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


# ============================================================================
# 2. gap_report()
# ============================================================================

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
    poya_suppressed_days: int = 0  # how many of the missing days were Poya
                                    # (informational -- these are NOT counted
                                    # in missing_days; see below)


def gap_report(
    engine: "sa.engine.Engine | None" = None,
    *,
    source: str = "HARTI",
) -> dict:
    """Scan PriceObservations for missing ObservedDate runs per
    (ExternalCommodityName, MarketId, Source), Poya-suppressed (see
    poya.expected_market_closed).

    A "run" of a series is its own sorted, distinct ObservedDate list.
    Gaps are computed on CALENDAR days minus Poya days that fall strictly
    between two consecutive observed dates -- i.e. a Poya day sitting
    inside an otherwise-1-day gap makes that gap disappear entirely (0
    missing days = no entry at all), not just downgrade its tier.

    Series with fewer than MIN_OBSERVATIONS_FOR_GAP_SCAN observations are
    skipped (cold-start -- not enough history to call anything a "gap"
    rather than "this series barely exists yet"); counted separately under
    `insufficient_history`.

    Returns a structured dict (never raises for gap findings themselves --
    an 8+ day ERROR-tier gap is a report entry requiring manual ack, not an
    exception that kills the ingestion run):
      {
        "entries": [GapEntry-shaped dicts, sorted by crop/market/gap_start],
        "n_info": int, "n_warning": int, "n_error": int,
        "insufficient_history": [{"crop_label", "market_name", "source",
                                    "n_observations"}] ,
        "series_scanned": int,
      }

    Each ERROR-tier entry is also logged at ERROR level (and WARNING/INFO
    similarly) so the ingestion log (this project's "IngestionLog table" --
    see canonical.py) carries the same signal as the returned report.
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


# ============================================================================
# 3. Outlier hold
# ============================================================================

OUTLIER_ROLLING_WINDOW_DAYS = 90
OUTLIER_IQR_MULTIPLIER = 4.0
MIN_OBSERVATIONS_FOR_OUTLIER_CHECK = 20  # cold-start floor within the window


def _iqr_median(values: Sequence[float]) -> "tuple[float, float] | None":
    """Return (median, iqr) for a sequence, or None if too few points.
    Pure Python (no numpy dependency for this small a computation) --
    linear-interpolation-free "exclusive" quartile method (matches the
    common textbook definition; fine for a coarse 4x-IQR fence, not a
    statistics-library-grade quantile implementation).
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

    LEAKAGE DISCIPLINE: for a candidate observation at ObservedDate T, the
    reference window is every observation of the same (CropId, MarketId,
    Source) with T-90 <= ObservedDate < T (strictly BEFORE T) -- the
    candidate itself is never included in its own reference statistics,
    and nothing at or after T is ever read. This is computed in Python
    per-series (not a SQL window function) specifically so the boundary is
    exact and easy to pin in a test (see TestNoLeakage).

    Rows with CropId IS NULL are skipped (an outlier fence needs a stable
    per-crop identity; ExternalCommodityName is a fallback identity but
    CropId is the canonical one once resolved -- an unresolved-crop row is
    already flagged elsewhere via heal_price_observation_crops(), not
    silently re-purposed as an outlier-check key here).

    Args:
        engine:   SQLAlchemy engine; created from config if None.
        source:   Source to scan (default "HARTI").
        dry_run:  If True, compute and report flags but do not write
                  IsUnitConfirmed=0 to the DB.

    Returns:
        {
          "flagged": [ {id, crop_id, market_id, external_commodity_name,
                         observed_date, midpoint, rolling_median,
                         rolling_iqr, threshold} , ... ],
          "series_checked": int,
          "series_skipped_cold_start": int,
          "n_flagged": int,
        }
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
            # Strictly BEFORE t -- leakage guard. obs is sorted, so this is
            # a simple prefix scan; O(n) per candidate is fine at this
            # corpus scale (thousands, not millions, of rows per series).
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
    """Admin helper: sets IsUnitConfirmed=1 for exactly one row, by Id.

    Deliberately single-row and parameterized -- no bulk "unquarantine
    everything matching X" call is exposed here, so clearing a hold is
    always a specific, auditable, one-at-a-time human decision (mirrors
    this project's existing "no silent bulk re-map" contract, e.g.
    canonical.heal_price_observation_crops()'s NULL-only guard).

    Returns True if a row was actually updated (Id existed), False
    otherwise.
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


# ============================================================================
# 4. assert_no_source_duplicates()
# ============================================================================

# R2 Step 2 (DEC -> PriceObservations mirror): the ONE named pair of sources
# that may legitimately coexist at the same (CropId, ObservedDate, MarketId)
# triple -- both cover Dambulla by design (HARTI's own bulletin Dambulla
# column, since Step 6; the DEC mirror, additive). A triple whose source set
# is anything OTHER than exactly this pair (a 3rd source, or two sources that
# are not this specific pair) is still a real violation -- see module
# docstring Section 5 for the full rationale.
# (Kept for reference/back-compat in messages; the operative structure is the
# per-market _ADJUDICATED_OVERLAPS map below.)
_ALLOWED_COEXISTING_SOURCES = frozenset({"HARTI", "DAMBULLA_DEC"})

# SF2 (reviewer should-fix, R2 Step 2 gate) + CBSL extension (2026-07-22): the
# adjudicated overlaps are scoped PER MARKET row -- an allowed source pair
# appearing at any market not adjudicated for it is not the by-design overlap,
# it is exactly the double-write bug this check exists to catch (e.g. a
# hypothetical mis-resolved-market mirror bug writing DEC rows at Pettah).
#
# The CBSL Daily Price Report source (feat/cbsl-price-parser) deliberately
# observes markets other sources already cover -- that is its cross-validation
# value -- so each market row's adjudicated set below is the FULL set of
# sources allowed to coexist there; a triple is allowed iff its actual source
# set is a SUBSET of its market's adjudicated set ({HARTI, DAMBULLA_DEC} at
# Dambulla stays allowed with or without CBSL present):
#   MKT00000001 Dambulla DEC:              DAMBULLA_DEC + HARTI + CBSL
#   MKT00000004 Pettah (HARTI wholesale):  HARTI + CBSL
#   MKT00000005 Narahenpita (HARTI retail): HARTI + CBSL
#
# Resolved BY CODE at call time (never a hardcoded GUID -- GUIDs are per-DB),
# fail-closed: a missing Markets row for any adjudicated code raises rather
# than silently widening (or silently narrowing to nothing) the allowance --
# see canonical.resolve_market_id_by_code.
_DAMBULLA_MARKET_CODE = "MKT00000001"

_ADJUDICATED_OVERLAPS: dict[str, frozenset] = {
    _DAMBULLA_MARKET_CODE: frozenset({"HARTI", "DAMBULLA_DEC", "CBSL"}),
    "MKT00000004": frozenset({"HARTI", "CBSL"}),  # Pettah (HARTI wholesale)
    "MKT00000005": frozenset({"HARTI", "CBSL"}),  # Narahenpita (HARTI retail)
}


def assert_no_source_duplicates(engine: "sa.engine.Engine | None" = None) -> int:
    """Assert no (CropId, ObservedDate, MarketId) triple in PriceObservations
    is present from a Source combination OTHER than the one adjudicated,
    by-design overlap ({"HARTI", "DAMBULLA_DEC"} AT THE DAMBULLA MARKET ONLY
    -- see _ALLOWED_COEXISTING_SOURCES / _DAMBULLA_MARKET_CODE).

    Two-step SQL: (1) find candidate triples with COUNT(DISTINCT Source) > 1
    (cheap, set-based); (2) pull the actual Source values for exactly those
    candidate triples and, in Python, flag only the ones whose (source SET,
    MarketId) is not precisely (the allowed pair, Dambulla). This still
    catches every double-write scenario the check was built for (a genuine
    3rd source, an unexpected 2-source combination, OR the adjudicated pair
    appearing at a market other than Dambulla) -- it only recognises the one
    pairing AND location this project has explicitly decided is correct, per
    R2 Step 2.

    Scoped to CropId IS NOT NULL (an unresolved crop has no stable identity
    to dedup on -- see heal_price_observation_crops() for that separate
    concern).

    Raises:
        RuntimeError if the Dambulla Markets row cannot be resolved (fail-
        closed -- see canonical.resolve_market_id_by_code).
        AssertionError listing the offending (CropId, ObservedDate,
        MarketId) triples (capped at 20 shown) if any are found.

    Returns:
        Number of distinct (CropId, ObservedDate, MarketId) triples
        examined (i.e. checked clean).
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
        # Allowed iff this market row has an adjudicated overlap set AND the
        # actual source set is within it (subset, so any adjudicated pair out
        # of a larger adjudicated trio stays allowed). Any source combination
        # at a non-adjudicated market remains a violation.
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


# ============================================================================
# 5. Macro point-in-time guard (P3 stub, generic + reusable now)
# ============================================================================

def assert_vintage_sane(
    publication_date: date,
    reference_period_start: date,
    *,
    now: "date | None" = None,
) -> None:
    """Reject a publication vintage that is impossible: published in the
    future (relative to `now`), or published before the reference period
    it is supposedly describing even started.

    Intended future caller (P3, not yet built): a macro-indicator row with
    columns (PublicationDate, ReferencePeriodStart) -- e.g. CBSL's monthly
    CPI print for "reference period = June 2026" cannot have
    PublicationDate before 2026-06-01 (you cannot publish a statistic about
    a period that hasn't started), and cannot have PublicationDate after
    today (the row would not exist yet in a point-in-time-correct world).

    Args:
        publication_date:        The vintage/publication date to validate.
        reference_period_start:  The start of the period the publication
                                  describes (e.g. first day of the month a
                                  CPI figure covers).
        now:                     Injected "today" for testability; real
                                  callers omit this and get date.today().

    Raises:
        ValueError if publication_date is after `now`, or before
        reference_period_start.
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
    """Reject an "effective from" date (e.g. BudgetEvents.EffectiveFrom)
    that lies in the future relative to `now` -- a point-in-time feature
    join must never treat a not-yet-effective policy/budget change as
    already in force.

    Intended future caller (P3, not yet built): BudgetEvents.EffectiveFrom
    guard at feature-build time, so a feature row for date T never reflects
    a budget change that (as of T) had not taken effect yet.

    Args:
        effective_from: The date a policy/event claims to take effect.
        now:            Injected "today" for testability; real callers
                         omit this and get date.today().

    Raises:
        ValueError if effective_from is after `now`.
    """
    today = now if now is not None else date.today()
    if effective_from > today:
        raise ValueError(
            f"assert_effective_not_future: effective_from {effective_from} "
            f"is in the future relative to now={today} -- a not-yet-"
            f"effective date must not be treated as already in force."
        )
