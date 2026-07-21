"""Post-ingestion data verification (R2 ingestion-log plan PR 2, ClickUp
per agriforecast-ingestion-log-plan.md).

Runs a fixed 14-check battery AFTER the daily ingestion pass (.NET Worker +
dec_mirror.py) and BEFORE build_features.py, and persists exactly one verdict
row to ``IngestionVerifications`` (schema owned by PR 1's migration
``CreateIngestionRunsAndVerifications`` -- this module is the sanctioned
Python writer of that .NET-schema'd table, see IngestionVerification.cs's own
docstring: "WRITTEN BY THE PYTHON SIDE later").

Read-only against every price/reference table -- the ONLY write this module
performs is the single verdict-row INSERT in ``persist_verdict``.

============================================================================
DESIGN NOTES / non-obvious threshold choices
============================================================================

Severity aggregation: overall = worst-of the 14 individual severities
(PASS < WARN < FAIL). A single FAIL anywhere fails the whole verification
run (run-daily.sh's ``set -euo pipefail`` + this CLI's exit code 1 blocks
build_features/training on a FAIL; a WARN exits 0 and is informational).

Check 1 (dec_row_count) thresholds are LIVE-MEASURED, not re-derived here
(see the plan doc): WARN outside [78, 96], FAIL if <20 or >115, and a
dedicated FAIL message when the count is exactly 0 (the feed simply hasn't
run yet for this date -- a distinct, more actionable message than "78-96
undershot").

Checks 3-5 (non_positive_prices, min_gt_max, extreme_outlier) and check 8
(harti_convention) are scoped to rows with ``RetrievedAtUtc`` within the
last 24 hours, computed ONCE at the top of ``run_all_verifications`` and
threaded through -- this is what makes check 3 "new rows only": the 196
known-good historical DAMBULLA_DEC zero-price rows (see data_quality.py /
dec_mirror.py "NON-POSITIVE PRICE HANDLING") were last (re-)ingested by a
backfill batch that ran many months before any live run of this verifier,
so they fall outside any 24h window and can never re-trigger this check --
no separate historical-date exclusion list is needed.

Check 6 (alias_regression) hardcodes the two adjudicated mapping facts this
project has already fixed regressions for (Garlic ext-47 -> VEG000071, the
Passion dual-ext-id-43/76 -> FRT000019 by-design pair) rather than deriving
them generically -- these are fixed named assertions per the task brief, not
a general alias-drift scanner (CommodityAliasResolver/heal_price_observation_crops
already own that broader concern).

Check 10 (mirror_currency)'s FAIL-vs-WARN split needs the PREVIOUS verdict
row (queried by ``ORDER BY RunUtc DESC`` BEFORE this run's own row is
inserted) so a single-run mirror lag (WARN, expected occasionally -- the
mirror runs after market ingest, so a same-day date can legitimately not be
mirrored yet within the same pass) is distinguished from a PERSISTENT lag
(the same mismatch also present last run -> FAIL, something is stuck).

Check 12 (mirror_fidelity) samples up to 50 rows (``MIRROR_FIDELITY_SAMPLE_SIZE``).
NOTE: at n=50, ANY single mismatch already exceeds the 1% escalation
threshold (1/50 = 2% > 1%), so in practice this check behaves as a strict
0-mismatches-tolerated gate: 0 -> PASS, >=1 -> FAIL. The WARN branch is
still implemented (mismatches present but the *fraction* <=1%) so the
check's stated contract ("WARN on any mismatch; FAIL if >1% of sample")
holds literally and remains correct if the sample size is ever widened
past ~100 -- it is simply unreachable at the pinned n=50, which is the
correct, conservative reading of the two clauses taken together rather
than a silently invented third threshold.

Check 13 (cross_source_dups) is the ONE check that reuses another module's
own exception as its failure signal (``data_quality.assert_no_source_duplicates``
raises ``AssertionError``) -- deliberately NOT wrapped in a generic
try/except at the ``run_all_verifications`` level; every other check is a
plain SELECT with no natural exception path, so a genuine unexpected
exception elsewhere (e.g. a DB connectivity failure) is left to propagate
all the way up to the CLI's own infra-error boundary (exit code 2) rather
than being silently laundered into a misleading "FAIL check" that would
make a totally-down DB look like "verification ran and found problems".

Check 14 (gap_tiers)'s "recent window" is 14 days (``GAP_TIER_RECENT_WINDOW_DAYS``),
NOT a reuse of ``data_quality.OUTLIER_ROLLING_WINDOW_DAYS`` (90 days) --
that was the first design tried here and live-testing this exact module
against the dev DB (2026-07-21) disproved it immediately: a 90-day window
over the full multi-market HARTI corpus (10 markets, dozens of crops,
Step 6's widening) turned up 49 ERROR-tier entries essentially every day,
making the check a permanent, uninformative WARN rather than signal. 14
days (two report cycles) is short enough that a gap closing this check
flags is still genuinely "recent news" and the check returns to PASS on
its own once nothing NEW has closed for two weeks, without requiring a
separate acknowledgement/clearing mechanism (gap_tiers has none by design
-- see the task brief, "report-only"). Never FAILs -- even a live
ERROR-tier gap in the recent window is WARN, not FAIL.
"""
from __future__ import annotations

import json
import logging
import time
import uuid
from dataclasses import dataclass, field
from datetime import date, datetime, timedelta, timezone

import sqlalchemy as sa

from . import data_quality as dq
from . import dec_mirror
from .db import get_engine

logger = logging.getLogger(__name__)

DEC_SOURCE = "DAMBULLA_DEC"
HARTI_SOURCE = "HARTI"

# --- Check 1: dec_row_count -------------------------------------------------
DEC_ROW_COUNT_WARN_LOW = 78
DEC_ROW_COUNT_WARN_HIGH = 96
DEC_ROW_COUNT_FAIL_LOW = 20
DEC_ROW_COUNT_FAIL_HIGH = 115

# --- Check 5: extreme_outlier ------------------------------------------------
EXTREME_OUTLIER_THRESHOLD = 5000

# --- Check 6: alias_regression -----------------------------------------------
GARLIC_EXTERNAL_PRODUCT_ID = 47
GARLIC_CROP_CODE = "VEG000071"
PASSION_EXTERNAL_PRODUCT_IDS = (43, 76)
PASSION_CROP_CODE = "FRT000019"

# --- Check 9: harti_freshness -------------------------------------------------
HARTI_FRESHNESS_WARN_MAX_DAYS = 2   # PASS if <= this
HARTI_FRESHNESS_FAIL_MAX_DAYS = 5   # WARN if <= this (and > WARN bound); FAIL beyond

# --- Check 12: mirror_fidelity -----------------------------------------------
MIRROR_FIDELITY_SAMPLE_SIZE = 50
MIRROR_FIDELITY_FAIL_FRACTION = 0.01  # see module docstring: unreachable at n=50

# --- Check 14: gap_tiers -------------------------------------------------------
# 14 days, NOT data_quality.OUTLIER_ROLLING_WINDOW_DAYS (90) -- see module
# docstring "Check 14" for the live-DB evidence that ruled out 90 days.
GAP_TIER_RECENT_WINDOW_DAYS = 14

_SEVERITY_RANK = {"PASS": 0, "WARN": 1, "FAIL": 2}


@dataclass
class CheckResult:
    name: str
    severity: str  # "PASS" | "WARN" | "FAIL"
    message: str
    counts: dict = field(default_factory=dict)

    def to_dict(self) -> dict:
        return {
            "name": self.name,
            "severity": self.severity,
            "message": self.message,
            "counts": self.counts,
        }


def _engine_or_default(engine: "sa.engine.Engine | None") -> sa.engine.Engine:
    return engine if engine is not None else get_engine()


def _coerce_date(value) -> "date | None":
    if value is None:
        return None
    if isinstance(value, str):
        return date.fromisoformat(value)
    if hasattr(value, "date") and not isinstance(value, date):
        return value.date()
    return value


# ============================================================================
# 1. dec_row_count
# ============================================================================

def _check_dec_row_count(engine, pipeline_date: date) -> CheckResult:
    with engine.connect() as conn:
        count = conn.execute(sa.text(
            "SELECT COUNT(*) FROM MarketPrices WHERE Source = :source AND PriceDate = :d"
        ), {"source": DEC_SOURCE, "d": pipeline_date}).scalar() or 0

    counts = {"row_count": int(count)}
    d = pipeline_date.isoformat()

    if count == 0:
        return CheckResult(
            "dec_row_count", "FAIL",
            f"No DAMBULLA_DEC MarketPrices rows found for {d} -- feed has not "
            f"run yet for this date, or ingestion failed.",
            counts,
        )
    if count < DEC_ROW_COUNT_FAIL_LOW or count > DEC_ROW_COUNT_FAIL_HIGH:
        return CheckResult(
            "dec_row_count", "FAIL",
            f"DAMBULLA_DEC row count {count} for {d} is outside the hard "
            f"bounds [{DEC_ROW_COUNT_FAIL_LOW}, {DEC_ROW_COUNT_FAIL_HIGH}].",
            counts,
        )
    if count < DEC_ROW_COUNT_WARN_LOW or count > DEC_ROW_COUNT_WARN_HIGH:
        return CheckResult(
            "dec_row_count", "WARN",
            f"DAMBULLA_DEC row count {count} for {d} is outside the expected "
            f"band [{DEC_ROW_COUNT_WARN_LOW}, {DEC_ROW_COUNT_WARN_HIGH}].",
            counts,
        )
    return CheckResult(
        "dec_row_count", "PASS",
        f"DAMBULLA_DEC row count {count} for {d} is within the expected band.",
        counts,
    )


# ============================================================================
# 2. dec_unmapped
# ============================================================================

def _check_dec_unmapped(engine, pipeline_date: date) -> CheckResult:
    with engine.connect() as conn:
        count = conn.execute(sa.text(
            """SELECT COUNT(*) FROM MarketPrices
               WHERE Source = :source AND PriceDate = :d AND CropId IS NULL"""
        ), {"source": DEC_SOURCE, "d": pipeline_date}).scalar() or 0

    counts = {"unmapped_count": int(count)}
    if count > 0:
        return CheckResult(
            "dec_unmapped", "FAIL",
            f"{count} DAMBULLA_DEC MarketPrices row(s) for {pipeline_date.isoformat()} "
            f"have CropId NULL (unmapped external commodity alias).",
            counts,
        )
    return CheckResult(
        "dec_unmapped", "PASS",
        "All DAMBULLA_DEC rows for the date have a resolved CropId.",
        counts,
    )


# ============================================================================
# 3. non_positive_prices  /  4. min_gt_max  /  5. extreme_outlier
# (scoped to RetrievedAtUtc >= since_utc -- "new rows only", see module docstring)
# ============================================================================

def _check_non_positive_prices(engine, since_utc: datetime) -> CheckResult:
    with engine.connect() as conn:
        mp = conn.execute(sa.text(
            """SELECT COUNT(*) FROM MarketPrices
               WHERE RetrievedAtUtc >= :since AND (MinPrice <= 0 OR MaxPrice <= 0)"""
        ), {"since": since_utc}).scalar() or 0
        po = conn.execute(sa.text(
            """SELECT COUNT(*) FROM PriceObservations
               WHERE RetrievedAtUtc >= :since
                 AND ((MinPrice IS NOT NULL AND MinPrice <= 0)
                   OR (MaxPrice IS NOT NULL AND MaxPrice <= 0))"""
        ), {"since": since_utc}).scalar() or 0

    total = int(mp) + int(po)
    counts = {"market_prices": int(mp), "price_observations": int(po), "total": total}
    if total > 0:
        return CheckResult(
            "non_positive_prices", "FAIL",
            f"{total} row(s) ingested in the last 24h have a non-positive "
            f"MinPrice/MaxPrice ({mp} MarketPrices, {po} PriceObservations).",
            counts,
        )
    return CheckResult(
        "non_positive_prices", "PASS",
        "No non-positive prices among rows ingested in the last 24h.",
        counts,
    )


def _check_min_gt_max(engine, since_utc: datetime) -> CheckResult:
    with engine.connect() as conn:
        mp = conn.execute(sa.text(
            """SELECT COUNT(*) FROM MarketPrices
               WHERE RetrievedAtUtc >= :since AND MinPrice > MaxPrice"""
        ), {"since": since_utc}).scalar() or 0
        po = conn.execute(sa.text(
            """SELECT COUNT(*) FROM PriceObservations
               WHERE RetrievedAtUtc >= :since
                 AND MinPrice IS NOT NULL AND MaxPrice IS NOT NULL
                 AND MinPrice > MaxPrice"""
        ), {"since": since_utc}).scalar() or 0

    total = int(mp) + int(po)
    counts = {"market_prices": int(mp), "price_observations": int(po), "total": total}
    if total > 0:
        return CheckResult(
            "min_gt_max", "FAIL",
            f"{total} row(s) ingested in the last 24h have MinPrice > MaxPrice "
            f"({mp} MarketPrices, {po} PriceObservations).",
            counts,
        )
    return CheckResult(
        "min_gt_max", "PASS",
        "No MinPrice > MaxPrice rows among rows ingested in the last 24h.",
        counts,
    )


def _check_extreme_outlier(engine, since_utc: datetime) -> CheckResult:
    with engine.connect() as conn:
        mp = conn.execute(sa.text(
            """SELECT COUNT(*) FROM MarketPrices
               WHERE RetrievedAtUtc >= :since AND MaxPrice > :threshold"""
        ), {"since": since_utc, "threshold": EXTREME_OUTLIER_THRESHOLD}).scalar() or 0
        po = conn.execute(sa.text(
            """SELECT COUNT(*) FROM PriceObservations
               WHERE RetrievedAtUtc >= :since AND MaxPrice > :threshold"""
        ), {"since": since_utc, "threshold": EXTREME_OUTLIER_THRESHOLD}).scalar() or 0

    total = int(mp) + int(po)
    counts = {"market_prices": int(mp), "price_observations": int(po), "total": total}
    if total > 0:
        return CheckResult(
            "extreme_outlier", "WARN",
            f"{total} row(s) ingested in the last 24h have MaxPrice > "
            f"Rs {EXTREME_OUTLIER_THRESHOLD} (all-time max is Rs 4,500) "
            f"({mp} MarketPrices, {po} PriceObservations).",
            counts,
        )
    return CheckResult(
        "extreme_outlier", "PASS",
        f"No rows ingested in the last 24h exceed Rs {EXTREME_OUTLIER_THRESHOLD}.",
        counts,
    )


# ============================================================================
# 6. alias_regression
# ============================================================================

def _check_alias_regression(engine) -> CheckResult:
    with engine.connect() as conn:
        garlic_rows = conn.execute(sa.text(
            """SELECT DISTINCT c.CropCode, c.Name
               FROM MarketPrices mp JOIN Crops c ON c.Id = mp.CropId
               WHERE mp.Source = :source AND mp.ExternalProductId = :ext"""
        ), {"source": DEC_SOURCE, "ext": GARLIC_EXTERNAL_PRODUCT_ID}).fetchall()

        passion_rows = conn.execute(sa.text(
            """SELECT DISTINCT mp.ExternalProductId, c.CropCode
               FROM MarketPrices mp JOIN Crops c ON c.Id = mp.CropId
               WHERE mp.Source = :source AND mp.ExternalProductId IN (:e1, :e2)"""
        ), {
            "source": DEC_SOURCE,
            "e1": PASSION_EXTERNAL_PRODUCT_IDS[0],
            "e2": PASSION_EXTERNAL_PRODUCT_IDS[1],
        }).fetchall()

    garlic_codes = {r[0] for r in garlic_rows}
    garlic_big_onion_hits = [r[1] for r in garlic_rows if r[1] and "big onion" in r[1].lower()]
    passion_mapping = {int(r[0]): r[1] for r in passion_rows}

    counts = {
        "garlic_crop_codes": sorted(garlic_codes),
        "garlic_big_onion_hits": garlic_big_onion_hits,
        "passion_mapping": {str(k): v for k, v in passion_mapping.items()},
    }

    if not garlic_rows:
        return CheckResult(
            "alias_regression", "FAIL",
            f"no ext-{GARLIC_EXTERNAL_PRODUCT_ID} mapping rows found (expected {GARLIC_CROP_CODE})",
            counts,
        )

    if garlic_big_onion_hits or garlic_codes != {GARLIC_CROP_CODE}:
        return CheckResult(
            "alias_regression", "FAIL",
            f"Garlic alias regression: DAMBULLA_DEC ext {GARLIC_EXTERNAL_PRODUCT_ID} "
            f"maps to CropCode(s) {sorted(garlic_codes)} "
            f"(expected exactly ['{GARLIC_CROP_CODE}']); "
            f"Big-Onion-named matches={garlic_big_onion_hits!r}.",
            counts,
        )

    passion_codes = set(passion_mapping.values())
    passion_ext_ids_seen = set(passion_mapping.keys())
    if passion_codes != {PASSION_CROP_CODE} or passion_ext_ids_seen != set(PASSION_EXTERNAL_PRODUCT_IDS):
        return CheckResult(
            "alias_regression", "WARN",
            f"Passion pair drift: DAMBULLA_DEC ext {PASSION_EXTERNAL_PRODUCT_IDS} "
            f"mapping={counts['passion_mapping']} (expected both -> "
            f"'{PASSION_CROP_CODE}').",
            counts,
        )

    return CheckResult(
        "alias_regression", "PASS",
        f"Garlic ext {GARLIC_EXTERNAL_PRODUCT_ID} -> {GARLIC_CROP_CODE} and Passion "
        f"pair {PASSION_EXTERNAL_PRODUCT_IDS} -> {PASSION_CROP_CODE} both hold.",
        counts,
    )


# ============================================================================
# 7. natural_key_dups
# ============================================================================

def _check_natural_key_dups(engine) -> CheckResult:
    with engine.connect() as conn:
        po_id_dups = conn.execute(sa.text(
            """SELECT COUNT(*) FROM (
                 SELECT MarketId, ExternalCommodityId, ObservedDate, Source
                 FROM PriceObservations
                 WHERE ExternalCommodityId IS NOT NULL
                 GROUP BY MarketId, ExternalCommodityId, ObservedDate, Source
                 HAVING COUNT(*) > 1
               ) dup"""
        )).scalar() or 0
        po_name_dups = conn.execute(sa.text(
            """SELECT COUNT(*) FROM (
                 SELECT MarketId, ExternalCommodityName, ObservedDate, Source
                 FROM PriceObservations
                 WHERE ExternalCommodityId IS NULL
                 GROUP BY MarketId, ExternalCommodityName, ObservedDate, Source
                 HAVING COUNT(*) > 1
               ) dup"""
        )).scalar() or 0
        mp_dups = conn.execute(sa.text(
            """SELECT COUNT(*) FROM (
                 SELECT Source, ExternalProductId, PriceDate
                 FROM MarketPrices
                 GROUP BY Source, ExternalProductId, PriceDate
                 HAVING COUNT(*) > 1
               ) dup"""
        )).scalar() or 0

    counts = {
        "po_id_keyed_dup_groups": int(po_id_dups),
        "po_name_keyed_dup_groups": int(po_name_dups),
        "market_prices_dup_groups": int(mp_dups),
    }
    total = counts["po_id_keyed_dup_groups"] + counts["po_name_keyed_dup_groups"] + counts["market_prices_dup_groups"]
    if total > 0:
        return CheckResult(
            "natural_key_dups", "FAIL",
            f"{total} duplicate group(s) found violating natural-key uniqueness "
            f"({counts['po_id_keyed_dup_groups']} PriceObservations id-keyed, "
            f"{counts['po_name_keyed_dup_groups']} PriceObservations name-keyed, "
            f"{counts['market_prices_dup_groups']} MarketPrices).",
            counts,
        )
    return CheckResult(
        "natural_key_dups", "PASS",
        "No natural-key duplicate groups in PriceObservations or MarketPrices.",
        counts,
    )


# ============================================================================
# 8. harti_convention
# ============================================================================

def _check_harti_convention(engine, since_utc: datetime) -> CheckResult:
    with engine.connect() as conn:
        checked = conn.execute(sa.text(
            "SELECT COUNT(*) FROM PriceObservations WHERE Source = :src AND RetrievedAtUtc >= :since"
        ), {"src": HARTI_SOURCE, "since": since_utc}).scalar() or 0
        violations = conn.execute(sa.text(
            """SELECT COUNT(*) FROM PriceObservations
               WHERE Source = :src AND RetrievedAtUtc >= :since
                 AND (MinPrice IS NULL OR MaxPrice IS NULL
                      OR WholesalePrice IS NOT NULL OR RetailPrice IS NOT NULL)"""
        ), {"src": HARTI_SOURCE, "since": since_utc}).scalar() or 0

    counts = {"checked": int(checked), "violations": int(violations)}
    if violations > 0:
        return CheckResult(
            "harti_convention", "FAIL",
            f"{violations} of {checked} HARTI PriceObservations row(s) ingested "
            f"in the last 24h violate the HARTI convention (Min/MaxPrice "
            f"non-null, Wholesale/RetailPrice null).",
            counts,
        )
    return CheckResult(
        "harti_convention", "PASS",
        f"{checked} HARTI PriceObservations row(s) ingested in the last 24h "
        f"all match the HARTI convention.",
        counts,
    )


# ============================================================================
# 9. harti_freshness
# ============================================================================

def _check_harti_freshness(engine, pipeline_date: date) -> CheckResult:
    with engine.connect() as conn:
        max_date_raw = conn.execute(sa.text(
            "SELECT MAX(ObservedDate) FROM PriceObservations WHERE Source = :src"
        ), {"src": HARTI_SOURCE}).scalar()

    if max_date_raw is None:
        return CheckResult(
            "harti_freshness", "FAIL",
            "No HARTI PriceObservations rows exist at all.",
            {"max_observed_date": None},
        )

    max_date = _coerce_date(max_date_raw)
    days_stale = (pipeline_date - max_date).days
    counts = {"max_observed_date": max_date.isoformat(), "days_stale": days_stale}

    if days_stale <= HARTI_FRESHNESS_WARN_MAX_DAYS:
        return CheckResult(
            "harti_freshness", "PASS",
            f"HARTI max ObservedDate is {max_date.isoformat()} ({days_stale} "
            f"day(s) behind pipeline_date {pipeline_date.isoformat()}).",
            counts,
        )
    if days_stale <= HARTI_FRESHNESS_FAIL_MAX_DAYS:
        return CheckResult(
            "harti_freshness", "WARN",
            f"HARTI max ObservedDate is {max_date.isoformat()} ({days_stale} "
            f"day(s) behind pipeline_date {pipeline_date.isoformat()}).",
            counts,
        )
    return CheckResult(
        "harti_freshness", "FAIL",
        f"HARTI max ObservedDate is {max_date.isoformat()} ({days_stale} "
        f"day(s) behind pipeline_date {pipeline_date.isoformat()}) -- exceeds "
        f"the {HARTI_FRESHNESS_FAIL_MAX_DAYS}-day fail bound.",
        counts,
    )


# ============================================================================
# 10. mirror_currency
# ============================================================================

def _check_mirror_currency(engine) -> CheckResult:
    with engine.connect() as conn:
        mp_max_raw = conn.execute(sa.text(
            "SELECT MAX(PriceDate) FROM MarketPrices WHERE Source = :src"
        ), {"src": DEC_SOURCE}).scalar()
        po_max_raw = conn.execute(sa.text(
            "SELECT MAX(ObservedDate) FROM PriceObservations WHERE Source = :src"
        ), {"src": DEC_SOURCE}).scalar()
        prev_row = conn.execute(sa.text(
            "SELECT TOP 1 ChecksJson FROM IngestionVerifications ORDER BY RunUtc DESC"
        )).fetchone()

    mp_max = _coerce_date(mp_max_raw)
    po_max = _coerce_date(po_max_raw)
    counts = {
        "market_prices_max_date": mp_max.isoformat() if mp_max else None,
        "price_observations_max_date": po_max.isoformat() if po_max else None,
    }

    if mp_max == po_max:
        return CheckResult(
            "mirror_currency", "PASS",
            f"DEC MarketPrices and PriceObservations max dates agree "
            f"({mp_max.isoformat() if mp_max else 'None'}).",
            counts,
        )

    previously_mismatched = _prior_mirror_currency_mismatched(prev_row)
    message = (
        f"DEC mirror currency mismatch: MarketPrices max={mp_max.isoformat() if mp_max else None}, "
        f"PriceObservations max={po_max.isoformat() if po_max else None}."
    )
    if previously_mismatched:
        return CheckResult(
            "mirror_currency", "FAIL",
            message + " Also mismatched in the previous verification run -- persistent lag.",
            counts,
        )
    return CheckResult(
        "mirror_currency", "WARN",
        message + " First occurrence this run -- likely a single-pass lag.",
        counts,
    )


def _prior_mirror_currency_mismatched(prev_row) -> bool:
    """Inspect the previous IngestionVerifications row's ChecksJson for a
    mirror_currency entry that was already WARN/FAIL last time -- see module
    docstring "Check 10" note. Defensive: any parse failure (malformed JSON,
    missing row, missing entry) is treated as "not previously mismatched"
    (fail-open towards WARN, never a spurious FAIL from a parsing accident).
    """
    if prev_row is None:
        return False
    try:
        prev_checks = json.loads(prev_row[0])
    except (TypeError, ValueError, json.JSONDecodeError):
        return False
    for entry in prev_checks or []:
        if isinstance(entry, dict) and entry.get("name") == "mirror_currency":
            return entry.get("severity") in ("WARN", "FAIL")
    return False


# ============================================================================
# 11. mirror_idempotency
# ============================================================================

def _check_mirror_idempotency(engine) -> CheckResult:
    report = dec_mirror.dry_run_report(engine)
    would_insert = int(report["would_insert"])
    counts = {"would_insert": would_insert}
    if would_insert > 0:
        return CheckResult(
            "mirror_idempotency", "FAIL",
            f"dec_mirror.dry_run_report reports {would_insert} row(s) still "
            f"pending mirroring -- the daily mirror pass did not run, or did "
            f"not fully catch up, before this verification ran.",
            counts,
        )
    return CheckResult(
        "mirror_idempotency", "PASS",
        "dec_mirror reports 0 rows pending -- fully caught up.",
        counts,
    )


# ============================================================================
# 12. mirror_fidelity
# ============================================================================

def _check_mirror_fidelity(engine) -> CheckResult:
    with engine.connect() as conn:
        rows = conn.execute(sa.text(
            """SELECT TOP (:n) po.MinPrice, po.MaxPrice, po.CropId,
                      mp.MinPrice, mp.MaxPrice, mp.CropId
               FROM PriceObservations po
               JOIN MarketPrices mp
                 ON mp.Source = po.Source
                AND mp.ExternalProductId = po.ExternalCommodityId
                AND mp.PriceDate = po.ObservedDate
               WHERE po.Source = :src
               ORDER BY po.ObservedDate DESC, po.Id"""
        ), {"n": MIRROR_FIDELITY_SAMPLE_SIZE, "src": DEC_SOURCE}).fetchall()

    sample_size = len(rows)
    if sample_size == 0:
        return CheckResult(
            "mirror_fidelity", "PASS",
            "No mirrored DEC rows exist yet to sample.",
            {"sample_size": 0, "mismatches": 0},
        )

    mismatches = 0
    for po_min, po_max, po_crop, mp_min, mp_max, mp_crop in rows:
        if po_min != mp_min or po_max != mp_max or str(po_crop) != str(mp_crop):
            mismatches += 1

    fraction = mismatches / sample_size
    counts = {"sample_size": sample_size, "mismatches": mismatches, "fraction": fraction}

    if mismatches == 0:
        return CheckResult(
            "mirror_fidelity", "PASS",
            f"0/{sample_size} sampled mirrored DEC rows mismatch their MarketPrices source.",
            counts,
        )
    if fraction > MIRROR_FIDELITY_FAIL_FRACTION:
        return CheckResult(
            "mirror_fidelity", "FAIL",
            f"{mismatches}/{sample_size} ({fraction:.1%}) sampled mirrored DEC "
            f"rows mismatch their MarketPrices source -- exceeds the "
            f"{MIRROR_FIDELITY_FAIL_FRACTION:.0%} fail threshold.",
            counts,
        )
    return CheckResult(
        "mirror_fidelity", "WARN",
        f"{mismatches}/{sample_size} ({fraction:.1%}) sampled mirrored DEC "
        f"rows mismatch their MarketPrices source.",
        counts,
    )


# ============================================================================
# 13. cross_source_dups
# ============================================================================

def _check_cross_source_dups(engine) -> CheckResult:
    try:
        n = dq.assert_no_source_duplicates(engine)
    except AssertionError as exc:
        return CheckResult("cross_source_dups", "FAIL", str(exc)[:900], {})
    return CheckResult(
        "cross_source_dups", "PASS",
        f"{n} (CropId, ObservedDate, MarketId) triples examined, no "
        f"unexpected cross-source duplicates.",
        {"triples_examined": int(n)},
    )


# ============================================================================
# 14. gap_tiers
# ============================================================================

def _check_gap_tiers(engine, pipeline_date: date) -> CheckResult:
    report = dq.gap_report(engine, source=HARTI_SOURCE)
    window_start = pipeline_date - timedelta(days=GAP_TIER_RECENT_WINDOW_DAYS)
    recent_errors = [
        e for e in report["entries"]
        if e["tier"] == "ERROR" and date.fromisoformat(e["gap_end"]) >= window_start
    ]
    counts = {
        "recent_error_entries": len(recent_errors),
        "window_days": GAP_TIER_RECENT_WINDOW_DAYS,
        "series_scanned": report["series_scanned"],
    }
    if recent_errors:
        return CheckResult(
            "gap_tiers", "WARN",
            f"{len(recent_errors)} ERROR-tier gap(s) ending within the last "
            f"{GAP_TIER_RECENT_WINDOW_DAYS} days (report-only, never FAILs).",
            counts,
        )
    return CheckResult(
        "gap_tiers", "PASS",
        f"No ERROR-tier gaps ending within the last {GAP_TIER_RECENT_WINDOW_DAYS} days.",
        counts,
    )


# ============================================================================
# Orchestration
# ============================================================================

def run_all_verifications(
    engine: "sa.engine.Engine | None" = None,
    *,
    pipeline_date: "date | None" = None,
    batch_id: "uuid.UUID | str | None" = None,
) -> dict:
    """Run the full 14-check battery and return a structured report.

    Read-only: never writes. ``pipeline_date`` defaults to
    ``datetime.now(timezone.utc).date()`` -- deliberately UTC, NOT the
    runner's local date (``date.today()``). PriceDate/ObservedDate in the DB
    and the DEC mirror's own vintage are UTC-anchored, and dec_row_count /
    dec_unmapped filter ``PriceDate = pipeline_date`` directly -- a runner
    whose local calendar date lags UTC (e.g. a US-timezone box a few hours
    before UTC midnight) would otherwise verify the WRONG market date and
    spuriously FAIL "no rows for date" against a date that simply hasn't
    happened yet in UTC. (The date being verified is still, in practice,
    "today" for the daily cron; the CLI can also point at any past date for
    a manual re-check.) ``batch_id`` is accepted for API symmetry with
    ``persist_verdict`` but is NOT included in the returned dict (kept to
    exactly the shape the caller/tests expect).

    Returns:
        {
          "overall": "PASS" | "WARN" | "FAIL",   # worst-of the 14 checks
          "checks": [ {name, severity, message, counts}, ... ],  # in fixed order
          "n_pass": int, "n_warn": int, "n_fail": int,
          "pipeline_date": "YYYY-MM-DD",
          "duration_ms": int,
        }
    """
    eng = _engine_or_default(engine)

    start = time.monotonic()
    now_utc = datetime.now(timezone.utc)
    # UTC, not date.today() -- see docstring above (S1 review fix).
    pd = pipeline_date if pipeline_date is not None else now_utc.date()
    since_24h = now_utc - timedelta(hours=24)

    # Fixed order -- also the order persisted in ChecksJson and printed by the CLI.
    checks: list[CheckResult] = [
        _check_dec_row_count(eng, pd),
        _check_dec_unmapped(eng, pd),
        _check_non_positive_prices(eng, since_24h),
        _check_min_gt_max(eng, since_24h),
        _check_extreme_outlier(eng, since_24h),
        _check_alias_regression(eng),
        _check_natural_key_dups(eng),
        _check_harti_convention(eng, since_24h),
        _check_harti_freshness(eng, pd),
        _check_mirror_currency(eng),
        _check_mirror_idempotency(eng),
        _check_mirror_fidelity(eng),
        _check_cross_source_dups(eng),
        _check_gap_tiers(eng, pd),
    ]

    n_pass = sum(1 for c in checks if c.severity == "PASS")
    n_warn = sum(1 for c in checks if c.severity == "WARN")
    n_fail = sum(1 for c in checks if c.severity == "FAIL")
    overall = max(checks, key=lambda c: _SEVERITY_RANK[c.severity]).severity if checks else "PASS"

    duration_ms = int((time.monotonic() - start) * 1000)

    logger.info(
        "ingest_verify.run_all_verifications: pipeline_date=%s overall=%s "
        "pass=%d warn=%d fail=%d duration_ms=%d",
        pd.isoformat(), overall, n_pass, n_warn, n_fail, duration_ms,
    )

    return {
        "overall": overall,
        "checks": [c.to_dict() for c in checks],
        "n_pass": n_pass,
        "n_warn": n_warn,
        "n_fail": n_fail,
        "pipeline_date": pd.isoformat(),
        "duration_ms": duration_ms,
    }


_OVERALL_STATUS_CODE = {"PASS": 0, "WARN": 1, "FAIL": 2}


def _build_summary(result: dict) -> str:
    """Short, human-facing, PATH-FREE summary line for the Summary column
    (<=1000 chars, DB-enforced by nvarchar(1000) but also truncated here
    defensively). Only check NAMES (not full messages) are included for
    non-PASS checks -- full detail lives in ChecksJson, so this line can
    never accidentally carry a path/traceback fragment from a check message.
    """
    overall = result["overall"]
    pd = result["pipeline_date"]
    line = (
        f"{overall}: {result['n_pass']} pass / {result['n_warn']} warn / "
        f"{result['n_fail']} fail (pipeline_date={pd})"
    )
    non_pass = [c["name"] for c in result["checks"] if c["severity"] != "PASS"]
    if non_pass:
        line += " -- non-pass checks: " + ", ".join(non_pass)
    return line[:1000]


def persist_verdict(
    engine: "sa.engine.Engine | None" = None,
    result: "dict | None" = None,
    *,
    batch_id: "uuid.UUID | str | None" = None,
) -> uuid.UUID:
    """Insert exactly one verdict row into IngestionVerifications.

    ``result`` must be the dict returned by ``run_all_verifications``.
    RunUtc/CreatedAtUtc are stamped by SQL Server's SYSUTCDATETIME() (never
    a Python-side clock value) -- same convention as dec_mirror.py's
    RetrievedAtUtc, avoiding any Python/DB clock-skew concern. Id is
    generated here (uuid4) since the Python side, not EF, owns this insert
    (see IngestionVerification.cs's own docstring).

    Raises on any DB failure (caller -- the CLI -- is responsible for
    catching this and mapping it to exit code 2, distinct from a
    verification FAIL which is exit code 1).
    """
    if result is None:
        raise ValueError("persist_verdict: result is required")
    eng = _engine_or_default(engine)

    verdict_id = uuid.uuid4()
    summary = _build_summary(result)
    checks_json = json.dumps(result["checks"])
    pipeline_date = date.fromisoformat(result["pipeline_date"])

    with eng.begin() as conn:
        conn.execute(sa.text(
            """INSERT INTO IngestionVerifications
                 (Id, BatchId, PipelineDate, RunUtc, OverallStatus,
                  NChecksPass, NChecksWarn, NChecksFail, Summary, ChecksJson,
                  DurationMs, CreatedAtUtc)
               VALUES
                 (:id, :batch_id, :pipeline_date, SYSUTCDATETIME(), :overall_status,
                  :n_pass, :n_warn, :n_fail, :summary, :checks_json,
                  :duration_ms, SYSUTCDATETIME())"""
        ), {
            "id": str(verdict_id),
            "batch_id": str(batch_id) if batch_id else None,
            "pipeline_date": pipeline_date,
            "overall_status": _OVERALL_STATUS_CODE[result["overall"]],
            "n_pass": result["n_pass"],
            "n_warn": result["n_warn"],
            "n_fail": result["n_fail"],
            "summary": summary,
            "checks_json": checks_json,
            "duration_ms": result["duration_ms"],
        })

    logger.info("ingest_verify.persist_verdict: inserted verdict %s (overall=%s)",
                verdict_id, result["overall"])
    return verdict_id
