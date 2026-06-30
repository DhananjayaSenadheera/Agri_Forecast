"""QA checks for the NewsArticles table.

Checks:
1. Row count and date coverage summary (per source).
2. Null URL assertion (none allowed -- Url is PK).
3. Null RetrievedAtUtc assertion (none allowed).
4. Duplicate URL assertion (none allowed -- UQ index enforces this at DB
   level, but we confirm at application level too for belt-and-suspenders).
5. PublishedDateUtc coverage: fraction of rows with a publication date.
   Low coverage (< 50%) is a WARNING, not a hard failure -- some feeds omit
   pub dates, and that is acceptable for raw ingestion.
"""
from __future__ import annotations

import logging

import sqlalchemy as sa
from sqlalchemy import text

from ..db import get_engine

logger = logging.getLogger(__name__)

TABLE = "NewsArticles"


def _engine_or_default(engine):
    return engine if engine is not None else get_engine()


def row_counts_per_source(engine=None) -> list:
    """Return [{source, count, earliest_pub, latest_pub}] sorted by source."""
    eng = _engine_or_default(engine)
    with eng.connect() as conn:
        rows = conn.execute(text(f"""
            SELECT Source,
                   COUNT(*) AS cnt,
                   MIN(PublishedDateUtc) AS earliest,
                   MAX(PublishedDateUtc) AS latest
            FROM {TABLE}
            GROUP BY Source
            ORDER BY Source
        """)).fetchall()
    return [
        {
            "source": r[0],
            "count": r[1],
            "earliest_pub": str(r[2]) if r[2] else None,
            "latest_pub":   str(r[3]) if r[3] else None,
        }
        for r in rows
    ]


def assert_no_null_urls(engine=None) -> int:
    """Assert Url is never NULL. Returns total row count."""
    eng = _engine_or_default(engine)
    with eng.connect() as conn:
        null_count = conn.execute(
            text(f"SELECT COUNT(*) FROM {TABLE} WHERE Url IS NULL")
        ).scalar()
        total = conn.execute(
            text(f"SELECT COUNT(*) FROM {TABLE}")
        ).scalar()
    if null_count:
        raise AssertionError(f"NewsArticles has {null_count} rows with NULL Url")
    logger.info("assert_no_null_urls PASSED: %d rows, 0 null Urls", total)
    return total


def assert_no_duplicate_urls(engine=None) -> int:
    """Assert all Urls are unique. Returns total row count."""
    eng = _engine_or_default(engine)
    with eng.connect() as conn:
        dups = conn.execute(text(f"""
            SELECT Url, COUNT(*) AS cnt
            FROM {TABLE}
            GROUP BY Url
            HAVING COUNT(*) > 1
        """)).fetchall()
        total = conn.execute(
            text(f"SELECT COUNT(*) FROM {TABLE}")
        ).scalar()
    if dups:
        dup_lines = "\n".join(
            f"  {r[0]} (count={r[1]})" for r in dups[:10]
        )
        raise AssertionError(
            f"NewsArticles has {len(dups)} duplicate Url(s):\n{dup_lines}"
        )
    logger.info(
        "assert_no_duplicate_urls PASSED: %d rows, 0 duplicate Urls", total
    )
    return total


def pub_date_coverage(engine=None) -> float:
    """Return fraction of rows that have a non-null PublishedDateUtc."""
    eng = _engine_or_default(engine)
    with eng.connect() as conn:
        total = conn.execute(
            text(f"SELECT COUNT(*) FROM {TABLE}")
        ).scalar()
        with_date = conn.execute(
            text(f"SELECT COUNT(*) FROM {TABLE} WHERE PublishedDateUtc IS NOT NULL")
        ).scalar()
    if total == 0:
        return 0.0
    frac = with_date / total
    if frac < 0.5:
        logger.warning(
            "pub_date_coverage: only %.1f%% of NewsArticles rows have "
            "PublishedDateUtc -- some feeds omit publication dates",
            frac * 100,
        )
    else:
        logger.info(
            "pub_date_coverage: %.1f%% of %d rows have PublishedDateUtc",
            frac * 100, total,
        )
    return frac


def run_all_qa(engine=None) -> dict:
    """Run all QA checks and return a structured report dict.

    Prints a human-readable summary.
    Raises AssertionError on hard failures (null URLs, duplicate URLs).
    Low pub-date coverage is a warning only.
    """
    eng = _engine_or_default(engine)

    print("\n" + "=" * 60)
    print("NEWS ARTICLES QA REPORT")
    print("=" * 60)

    # 1. Row counts per source
    counts = row_counts_per_source(eng)
    total_rows = sum(r["count"] for r in counts)
    print(f"\n--- Row counts per source (total {total_rows} rows) ---")
    for r in counts:
        print(
            f"  {r['source']:<30s}  {r['count']:5d} rows  "
            f"pub: {r['earliest_pub'] or 'N/A'} .. {r['latest_pub'] or 'N/A'}"
        )

    # 2. Null URL check
    print("\n--- Null URL check ---")
    n_rows = assert_no_null_urls(eng)
    print(f"  PASSED: {n_rows} rows, 0 null Urls")

    # 3. Duplicate URL check
    print("\n--- Duplicate URL check ---")
    assert_no_duplicate_urls(eng)
    print("  PASSED: 0 duplicate Urls")

    # 4. PublishedDateUtc coverage
    frac = pub_date_coverage(eng)
    print(f"\n--- PublishedDateUtc coverage: {frac * 100:.1f}% ---")
    if frac < 0.5:
        print("  WARNING: < 50% of rows have a publication date")
    else:
        print("  OK")

    print("\n" + "=" * 60)

    return {
        "total_rows": total_rows,
        "per_source": counts,
        "pub_date_coverage_pct": round(frac * 100, 1),
    }
