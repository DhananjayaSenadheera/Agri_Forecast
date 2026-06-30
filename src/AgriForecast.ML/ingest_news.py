"""News RSS ingestion pipeline entry point.

Usage:
    # Full live run (network + DB required):
    python ingest_news.py

    # Dry run: fetch feeds but do NOT write to DB:
    python ingest_news.py --dry-run

    # Verbose output:
    python ingest_news.py --log-level DEBUG

Options:
    --dry-run          Fetch and parse feeds but do NOT write to DB.
    --skip-qa          Skip QA checks after loading (faster debugging).
    --log-level LEVEL  Logging verbosity: DEBUG / INFO / WARNING / ERROR
                       (default: INFO).

Live environment requirements:
    Network     -- to reach external RSS URLs.
    SQL Server  -- via settings in AgriForecast.API/appsettings.json or
                   AGRI_DB_* environment variables (same as all other Python
                   pipeline scripts).

Output printed to stdout:
    Feeds attempted, entries fetched per feed, new rows inserted, dups skipped.
"""
from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path


def _setup_logging(level: str) -> None:
    logging.basicConfig(
        level=getattr(logging, level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)-8s %(name)s -- %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )
    logging.getLogger("urllib3").setLevel(logging.WARNING)
    logging.getLogger("urllib").setLevel(logging.WARNING)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="AgriForecast news RSS ingestion pipeline",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Fetch feeds but do NOT write to DB",
    )
    parser.add_argument(
        "--skip-qa",
        action="store_true",
        help="Skip QA checks after loading",
    )
    parser.add_argument(
        "--log-level",
        default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Logging verbosity (default: INFO)",
    )
    args = parser.parse_args()
    _setup_logging(args.log_level)
    log = logging.getLogger(__name__)

    from agriforecast_ml.news import fetcher, loader, qa
    from agriforecast_ml.news.feeds import FEEDS

    # ------------------------------------------------------------------ #
    # Step 1: Fetch feeds
    # ------------------------------------------------------------------ #
    log.info("Step 1: Fetching %d RSS feeds...", len(FEEDS))
    articles, coverage = fetcher.fetch_all(FEEDS)
    log.info(
        "Step 1 complete: %d unique articles fetched across %d feeds",
        len(articles), len(FEEDS),
    )

    n_feeds_with_data = sum(1 for v in coverage.values() if v > 0)
    n_feeds_empty = len(FEEDS) - n_feeds_with_data

    # ------------------------------------------------------------------ #
    # Step 2: Load into DB
    # ------------------------------------------------------------------ #
    if args.dry_run:
        log.info("--dry-run: skipping DB write")
        counters = {"inserted": 0, "dup_skipped": 0, "total": len(articles)}
    else:
        log.info("Step 2: Upserting %d articles into NewsArticles...", len(articles))
        counters = loader.upsert_articles(articles)
        log.info("Step 2 complete: %s", counters)

    # ------------------------------------------------------------------ #
    # Step 3: QA (skipped in dry-run)
    # ------------------------------------------------------------------ #
    if args.skip_qa or args.dry_run:
        if args.dry_run:
            log.info("Dry run -- skipping QA (no DB writes)")
    else:
        log.info("Step 3: Running QA checks on NewsArticles...")
        try:
            report = qa.run_all_qa()
            log.info(
                "QA PASSED -- %d total rows, %.1f%% with pub date",
                report["total_rows"], report["pub_date_coverage_pct"],
            )
        except AssertionError as exc:
            log.error("QA FAILED: %s", exc)
            sys.exit(2)

    # ------------------------------------------------------------------ #
    # Summary
    # ------------------------------------------------------------------ #
    print("\nNews ingestion summary")
    print("  Feeds attempted     :", len(FEEDS))
    print("  Feeds with data     :", n_feeds_with_data)
    print("  Feeds empty/down    :", n_feeds_empty)
    print("  Per-feed entry counts:")
    for source, count in sorted(coverage.items()):
        print(f"    {source:<30s}  {count:4d} entries")
    print("  Total unique fetched:", len(articles))
    print("  Inserted (new rows) :", counters["inserted"])
    print("  Dups skipped        :", counters["dup_skipped"])
    if args.dry_run:
        print("  [dry-run: no DB writes performed]")


if __name__ == "__main__":
    main()
