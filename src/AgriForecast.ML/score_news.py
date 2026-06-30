"""News sentiment scoring pipeline entry point (Phase 5 Chunk C2).

Reads NewsArticles -> scores each (VADER compound + topic flags) -> aggregates
to the national daily signal -> rebuilds the NewsSentimentDaily table.

Usage:
    # Full live run (DB required; NewsArticles must have rows):
    python score_news.py

    # Also write per-article compound scores back to NewsArticles.SentimentScore
    # (debug aid; the daily table is the real deliverable):
    python score_news.py --writeback-scores

    # Score + aggregate but do NOT write to DB (prints summary only):
    python score_news.py --dry-run

    # Verbose:
    python score_news.py --log-level DEBUG

Live environment requirements:
    SQL Server -- via AgriForecast.API/appsettings.json or AGRI_DB_* env vars
                  (same as all other Python pipeline scripts).

Output printed to stdout:
    articles scored, effective-date range, mean sentiment, per-topic day counts,
    rows written to NewsSentimentDaily.
"""
from __future__ import annotations

import argparse
import logging
import sys


def _setup_logging(level: str) -> None:
    logging.basicConfig(
        level=getattr(logging, level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)-8s %(name)s -- %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )


def main() -> None:
    parser = argparse.ArgumentParser(
        description="AgriForecast news sentiment scoring pipeline",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Score + aggregate but do NOT write to DB",
    )
    parser.add_argument(
        "--writeback-scores",
        action="store_true",
        help="Also write per-article SentimentScore back to NewsArticles (debug)",
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

    from agriforecast_ml.news import sentiment, store_sentiment
    from agriforecast_ml.news.sentiment import TOPIC_NAMES, _pascal

    # ------------------------------------------------------------------ #
    # Step 1: Load articles
    # ------------------------------------------------------------------ #
    log.info("Step 1: Loading NewsArticles...")
    try:
        articles = store_sentiment.load_articles()
    except Exception as exc:
        log.error("Failed to load NewsArticles: %s: %s", type(exc).__name__, exc)
        sys.exit(2)

    n_articles = len(articles)
    log.info("Step 1 complete: %d articles loaded", n_articles)
    if n_articles == 0:
        print("\nNo articles in NewsArticles -- nothing to score.")
        print("Run ingest_news.py first (requires network + feeds).")
        sys.exit(0)

    # ------------------------------------------------------------------ #
    # Step 2: Score + aggregate
    # ------------------------------------------------------------------ #
    log.info("Step 2: Scoring + aggregating...")
    scored, daily = sentiment.score_and_aggregate(articles)
    log.info("Step 2 complete: %d daily rows", len(daily))

    # ------------------------------------------------------------------ #
    # Step 3: Write
    # ------------------------------------------------------------------ #
    rows_written = 0
    if args.dry_run:
        log.info("--dry-run: skipping DB write")
    else:
        log.info("Step 3: Rebuilding NewsSentimentDaily...")
        rows_written = store_sentiment.write_daily(daily)
        if args.writeback_scores:
            n = store_sentiment.write_article_scores(scored)
            log.info("Wrote per-article scores back to NewsArticles: %d rows", n)

    # ------------------------------------------------------------------ #
    # Summary
    # ------------------------------------------------------------------ #
    print("\nNews sentiment scoring summary")
    print("  Articles scored      :", n_articles)
    if not daily.empty:
        print("  Effective-date range :", daily["Date"].min(), "..", daily["Date"].max())
        print(f"  Mean sentiment (all) : {scored['SentimentScore'].mean():+.4f}")
        print("  Daily rows           :", len(daily))
        print("  Per-topic day counts (days with >=1 flagged article):")
        for topic in TOPIC_NAMES:
            pas = _pascal(topic)
            n_days = int((daily[f"{pas}Count"] > 0).sum())
            n_arts = int(daily[f"{pas}Count"].sum())
            print(f"    {topic:<12s}  {n_days:4d} days  {n_arts:5d} articles")
    if args.dry_run:
        print("  [dry-run: no DB writes performed]")
    else:
        print("  Rows -> NewsSentimentDaily:", rows_written)


if __name__ == "__main__":
    main()
