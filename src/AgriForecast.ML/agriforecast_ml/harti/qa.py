"""HARTI backfill QA checks.

Checks run after the loader completes:
1. Row counts per crop / per year (HARTI rows only).
2. Gap report: list of (crop, gap_start, gap_end, gap_days) where gaps > 7 days.
3. Zero-duplicate assertion: no (CropId, PriceDate) has rows from BOTH HARTI
   and DAMBULLA_DEC.  Raises AssertionError if violated.

All output is printed + returned as structured dicts for programmatic use.
"""
from __future__ import annotations

import logging
from datetime import date

import sqlalchemy as sa

from ..db import get_engine

logger = logging.getLogger(__name__)

GAP_THRESHOLD_DAYS = 7   # gaps narrower than this are not reported
HARTI_SOURCE = "HARTI"
DEC_SOURCE = "DAMBULLA_DEC"


def _engine_or_default(engine):
    return engine if engine is not None else get_engine()


def row_counts_per_crop_year(engine=None) -> list[dict]:
    """Returns a list of {crop, year, count} dicts, HARTI source only."""
    eng = _engine_or_default(engine)
    with eng.connect() as conn:
        rows = conn.execute(sa.text("""
            SELECT c.Name AS crop,
                   YEAR(mp.PriceDate) AS yr,
                   COUNT(*) AS cnt
            FROM MarketPrices mp
            JOIN Crops c ON c.Id = mp.CropId
            WHERE mp.Source = :src
            GROUP BY c.Name, YEAR(mp.PriceDate)
            ORDER BY c.Name, YEAR(mp.PriceDate)
        """), {"src": HARTI_SOURCE}).fetchall()

    result = [{"crop": r[0], "year": r[1], "count": r[2]} for r in rows]
    return result


def gap_report(engine=None, threshold_days: int = GAP_THRESHOLD_DAYS) -> list[dict]:
    """Return gaps > threshold_days in HARTI price series per crop.

    Gaps in the series mean market-closed days, public holidays, or genuine
    missing PDFs.  Gaps <= 7 days are normal weekend / holiday closures.
    Larger gaps may indicate missing data downloads.
    """
    eng = _engine_or_default(engine)
    with eng.connect() as conn:
        rows = conn.execute(sa.text("""
            SELECT c.Name AS crop, mp.PriceDate
            FROM MarketPrices mp
            JOIN Crops c ON c.Id = mp.CropId
            WHERE mp.Source = :src
            ORDER BY c.Name, mp.PriceDate
        """), {"src": HARTI_SOURCE}).fetchall()

    # Group by crop
    from collections import defaultdict
    by_crop: dict[str, list[date]] = defaultdict(list)
    for r in rows:
        crop_name = r[0]
        price_date = r[1]
        if isinstance(price_date, str):
            price_date = date.fromisoformat(price_date)
        elif hasattr(price_date, "date"):
            price_date = price_date.date()
        by_crop[crop_name].append(price_date)

    gaps = []
    for crop, dates in by_crop.items():
        dates.sort()
        for i in range(1, len(dates)):
            gap = (dates[i] - dates[i - 1]).days
            if gap > threshold_days:
                gaps.append({
                    "crop": crop,
                    "gap_start": dates[i - 1].isoformat(),
                    "gap_end": dates[i].isoformat(),
                    "gap_days": gap,
                })

    return sorted(gaps, key=lambda g: (g["crop"], g["gap_start"]))


def assert_no_source_duplicates(engine=None) -> int:
    """Assert no (CropId, PriceDate) appears in both HARTI and DAMBULLA_DEC.

    This is the critical splice invariant: the feature build should never
    see two price rows for the same crop+date from different sources.

    Returns:
        Number of (crop, date) pairs checked (i.e. total HARTI dates).
    Raises:
        AssertionError with details if any duplicates are found.
    """
    eng = _engine_or_default(engine)
    with eng.connect() as conn:
        dups = conn.execute(sa.text("""
            SELECT c.Name AS crop,
                   CONVERT(varchar(10), h.PriceDate) AS price_date,
                   h.MinPrice AS harti_min,
                   h.MaxPrice AS harti_max,
                   d.MinPrice AS dec_min,
                   d.MaxPrice AS dec_max
            FROM MarketPrices h
            JOIN MarketPrices d
              ON h.CropId = d.CropId
             AND h.PriceDate = d.PriceDate
            JOIN Crops c ON c.Id = h.CropId
            WHERE h.Source = :harti
              AND d.Source = :dec
            ORDER BY c.Name, h.PriceDate
        """), {"harti": HARTI_SOURCE, "dec": DEC_SOURCE}).fetchall()

        total_harti = conn.execute(sa.text(
            "SELECT COUNT(*) FROM MarketPrices WHERE Source = :src"
        ), {"src": HARTI_SOURCE}).scalar()

    if dups:
        dup_lines = "\n".join(
            f"  {r[0]}  {r[1]}  harti=[{r[2]},{r[3]}]  dec=[{r[4]},{r[5]}]"
            for r in dups[:20]
        )
        raise AssertionError(
            f"SPLICE VIOLATION: {len(dups)} (crop, date) pairs exist in BOTH "
            f"HARTI and DAMBULLA_DEC sources:\n{dup_lines}"
            + (f"\n  ... and {len(dups) - 20} more" if len(dups) > 20 else "")
        )

    logger.info(
        "Zero-duplicate assertion PASSED: %d HARTI rows, 0 (crop,date) overlaps with DEC",
        total_harti,
    )
    return total_harti


def run_all_qa(engine=None, gap_threshold: int = GAP_THRESHOLD_DAYS) -> dict:
    """Run all QA checks and return a structured report dict.

    Also prints a human-readable summary.
    Raises AssertionError if the duplicate check fails.
    """
    eng = _engine_or_default(engine)

    print("\n" + "=" * 60)
    print("HARTI BACKFILL QA REPORT")
    print("=" * 60)

    # 1. Row counts per crop / year
    counts = row_counts_per_crop_year(eng)
    print("\n--- Row counts per crop/year (HARTI) ---")
    current_crop = None
    totals_by_crop: dict[str, int] = {}
    for r in counts:
        if r["crop"] != current_crop:
            current_crop = r["crop"]
            print(f"\n  {current_crop}:")
        print(f"    {r['year']}: {r['count']:4d} rows")
        totals_by_crop[r["crop"]] = totals_by_crop.get(r["crop"], 0) + r["count"]

    print("\n  TOTAL per crop:")
    grand_total = 0
    for crop, total in sorted(totals_by_crop.items()):
        print(f"    {crop}: {total:6d}")
        grand_total += total
    print(f"\n  GRAND TOTAL: {grand_total} HARTI rows")

    # 2. Gap report
    gaps = gap_report(eng, threshold_days=gap_threshold)
    print(f"\n--- Gap report (gaps > {gap_threshold} days) ---")
    if not gaps:
        print("  No significant gaps found.")
    else:
        prev_crop = None
        for g in gaps:
            if g["crop"] != prev_crop:
                prev_crop = g["crop"]
                print(f"\n  {g['crop']}:")
            print(f"    {g['gap_start']} → {g['gap_end']}  ({g['gap_days']} days)")
    print(f"\n  Total significant gaps: {len(gaps)}")

    # 3. Duplicate assertion
    print("\n--- Splice duplicate check (HARTI ∩ DEC) ---")
    try:
        n_harti = assert_no_source_duplicates(eng)
        print(f"  PASSED: {n_harti} HARTI rows, zero (crop,date) overlaps with DEC.")
        dup_check_passed = True
    except AssertionError as exc:
        print(f"  FAILED: {exc}")
        dup_check_passed = False
        raise

    print("\n" + "=" * 60)

    return {
        "row_counts": counts,
        "totals_by_crop": totals_by_crop,
        "grand_total": grand_total,
        "gaps": gaps,
        "n_gaps": len(gaps),
        "dup_check_passed": dup_check_passed,
        "n_harti_rows": grand_total,
    }
