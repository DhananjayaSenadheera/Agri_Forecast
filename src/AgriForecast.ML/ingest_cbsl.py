"""CBSL Daily Price Report ingestion entry point (capture-only, 2026-07-22).

Thin CLI over ingest_cbsl_service.run() — the same module the
/admin/ingest-cbsl FastAPI route uses, so CLI and Worker passes are one code
path.

Usage:
    # Incremental pass (default: last 7 days when no --since is given):
    python ingest_cbsl.py

    # Resume after a known date (the .NET watermark semantics):
    python ingest_cbsl.py --since 2026-07-14

    # Deliberate historical backfill (owner-approved runs only — the daily
    # pass never does this): a wide --since plus a raised --max-dates.
    python ingest_cbsl.py --since 2024-07-01 --max-dates 800

    # Parse-only from the existing cache (no network):
    python ingest_cbsl.py --no-download

    # Dry-run: parse + resolve but do NOT write to DB:
    python ingest_cbsl.py --dry-run
"""
from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path


def main() -> None:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--since", default=None,
                    help="ISO date lower bound (exclusive). Default: last 7 days.")
    ap.add_argument("--cache-dir", default=None,
                    help="PDF cache directory (default: ./cbsl_cache)")
    ap.add_argument("--max-dates", type=int, default=None,
                    help="Per-pass candidate-date cap (default 62; raise only "
                         "for a deliberate backfill)")
    ap.add_argument("--no-download", action="store_true",
                    help="Use the existing cache only (no network)")
    ap.add_argument("--dry-run", action="store_true",
                    help="Parse and resolve but do NOT write to DB")
    ap.add_argument("--log-level", default="INFO")
    args = ap.parse_args()

    logging.basicConfig(
        level=getattr(logging, args.log_level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)-8s %(name)s — %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    if args.max_dates is not None:
        from agriforecast_ml.cbsl_report import downloader
        downloader.DEFAULT_MAX_DATES_PER_PASS = args.max_dates

    import ingest_cbsl_service

    try:
        summary = ingest_cbsl_service.run(
            since_date=args.since,
            no_download=args.no_download,
            dry_run=args.dry_run,
            cache_dir=Path(args.cache_dir) if args.cache_dir else None,
        )
    except AssertionError as exc:
        print(f"FAILED data-quality gate: {exc}", file=sys.stderr)
        sys.exit(2)

    print(summary)


if __name__ == "__main__":
    main()
