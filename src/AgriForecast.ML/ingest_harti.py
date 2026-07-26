"""HARTI price backfill pipeline entry point.

Usage:
    # Full run (download all ~3009 PDFs + parse + load + QA):
    python ingest_harti.py

    # Slice validation — one calendar year only:
    python ingest_harti.py --slice-year 2019

    # Parse-only from already-downloaded cache (no network):
    python ingest_harti.py --no-download

    # Dry-run: parse + resolve CropIds but do NOT write to DB:
    python ingest_harti.py --dry-run

Options:
    --cache-dir PATH      Local PDF cache directory
                          (default: ./harti_cache)
    --slice-year YYYY     Only process PDFs from this calendar year
    --no-download         Skip scraping/downloading; use existing cache
    --dry-run             Parse and validate, but do NOT write to DB
    --log-level LEVEL     Logging verbosity (default: INFO)
    --skip-qa             Skip QA checks after loading (faster debugging)
"""
from __future__ import annotations

import argparse
import logging
import sys
from pathlib import Path

import requests


def _setup_logging(level: str) -> None:
    logging.basicConfig(
        level=getattr(logging, level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)-8s %(name)s — %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )
    # Quiet third-party noise
    logging.getLogger("pdfminer").setLevel(logging.ERROR)
    logging.getLogger("urllib3").setLevel(logging.WARNING)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="HARTI price backfill pipeline",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    parser.add_argument(
        "--cache-dir",
        type=Path,
        default=Path(__file__).parent / "harti_cache",
        help="Local PDF cache directory (default: ./harti_cache)",
    )
    parser.add_argument(
        "--slice-year",
        type=int,
        default=None,
        metavar="YYYY",
        help="Only process PDFs from this calendar year (for slice validation)",
    )
    parser.add_argument(
        "--no-download",
        action="store_true",
        help="Skip scraping/downloading; use existing cache only",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Parse and validate but do NOT write to DB",
    )
    parser.add_argument(
        "--log-level",
        default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Logging verbosity (default: INFO)",
    )
    parser.add_argument(
        "--skip-qa",
        action="store_true",
        help="Skip QA checks after loading",
    )
    args = parser.parse_args()

    _setup_logging(args.log_level)
    log = logging.getLogger(__name__)

    from agriforecast_ml.harti import downloader, parser as harti_parser, loader, qa

    # Step 1: Download / scrape PDFs
    cache_dir: Path = args.cache_dir
    cache_dir.mkdir(parents=True, exist_ok=True)

    if args.no_download:
        log.info("--no-download: scanning existing cache at %s", cache_dir)
        cached_pdfs = []
        for pdf_path in sorted(cache_dir.glob("harti_*.pdf")):
            date_str = pdf_path.stem.replace("harti_", "")
            # Validate date format
            try:
                from datetime import date as _date
                _date.fromisoformat(date_str)
                cached_pdfs.append((date_str, pdf_path))
            except ValueError:
                log.warning("Skipping non-standard filename: %s", pdf_path.name)
        log.info("Found %d PDFs in cache", len(cached_pdfs))
    else:
        session = requests.Session()
        log.info("Step 1: Scraping HARTI listing page for PDF links...")
        links = downloader.scrape_pdf_links(session)
        log.info("Found %d PDF links", len(links))

        # Apply slice filter before downloading
        if args.slice_year:
            links = [(d, u) for d, u in links if d.year == args.slice_year]
            log.info("Slice filter (year=%d): %d links remaining", args.slice_year, len(links))

        log.info("Step 1b: Downloading PDFs to %s ...", cache_dir)
        cached_pdfs = downloader.download_pdfs(cache_dir, links, session=session)

    # Apply slice filter to cached PDFs if no-download mode
    if args.slice_year and args.no_download:
        year_str = str(args.slice_year)
        cached_pdfs = [(d, p) for d, p in cached_pdfs if d.startswith(year_str)]
        log.info(
            "Slice filter (year=%d): %d cached PDFs", args.slice_year, len(cached_pdfs)
        )

    if not cached_pdfs:
        log.error("No PDFs to process — exiting")
        sys.exit(1)

    log.info("Step 1 complete: %d PDFs to parse", len(cached_pdfs))

    # Step 2: Parse PDFs
    log.info("Step 2: Parsing %d PDFs...", len(cached_pdfs))
    parsed_rows = harti_parser.parse_many(cached_pdfs, log_every=200)
    log.info("Step 2 complete: %d parsed price rows", len(parsed_rows))

    if not parsed_rows:
        log.warning("No price rows parsed — check PDF format or log for errors")

    # Quick summary
    from collections import Counter
    crop_counts = Counter(r.harti_label for r in parsed_rows)
    log.info("Parsed rows by HARTI crop label: %s", dict(sorted(crop_counts.items())))

    # Step 3: Load into DB (with splice rule + idempotent upsert)
    log.info(
        "Step 3: Loading into MarketPrices (Dambulla-only, back-compat; dry_run=%s)...",
        args.dry_run,
    )
    counters = loader.upsert_harti_prices(parsed_rows, dry_run=args.dry_run)
    log.info("Step 3 complete: %s", counters)

    # Step 3b: load ALL markets into PriceObservations. Additive to step 3, and the splice
    # rule does not apply here (see loader.py).
    log.info(
        "Step 3b: Loading into PriceObservations (all markets; dry_run=%s)...",
        args.dry_run,
    )
    po_counters = loader.upsert_harti_price_observations(parsed_rows, dry_run=args.dry_run)
    log.info("Step 3b complete: %s", po_counters)

    # Step 4: QA checks
    if args.skip_qa or args.dry_run:
        if args.dry_run:
            log.info("Dry run — skipping QA (no DB writes)")
        else:
            log.info("--skip-qa: skipping QA checks")
        print("\nPipeline complete.")
        print(f"  Parsed rows  : {len(parsed_rows)}")
        print(f"  MarketPrices (Dambulla only):")
        print(f"    Inserted     : {counters.get('inserted', 0)}")
        print(f"    Updated      : {counters.get('updated', 0)}")
        print(f"    Splice-skip  : {counters.get('skipped_splice', 0)}")
        print(f"    No-crop-skip : {counters.get('skipped_no_crop', 0)}")
        print(f"  PriceObservations (all markets):")
        print(f"    Inserted        : {po_counters.get('inserted', 0)}")
        print(f"    Updated         : {po_counters.get('updated', 0)}")
        print(f"    No-market-skip  : {po_counters.get('skipped_no_market', 0)}")
        return

    log.info("Step 4: Running QA checks...")
    from agriforecast_ml.db import get_engine
    eng = get_engine()
    try:
        report = qa.run_all_qa(engine=eng)
        log.info("QA PASSED — %d HARTI rows, %d gaps", report["grand_total"], report["n_gaps"])
    except AssertionError as exc:
        log.error("QA FAILED: %s", exc)
        sys.exit(2)

    # Step 4b: PriceObservations-scoped data-quality checks. assert_no_source_duplicates is a
    # hard fail and must run on every pass. gap_report() and flag_price_outliers() are
    # deliberately not called here: they return findings for manual acknowledgement and must
    # not gate this pipeline's exit code.
    from agriforecast_ml.data_quality import assert_no_source_duplicates
    log.info("Step 4b: Running PriceObservations cross-source duplicate check...")
    try:
        n_checked = assert_no_source_duplicates(eng)
        log.info("PriceObservations duplicate check PASSED — %d triples checked", n_checked)
    except AssertionError as exc:
        log.error("PriceObservations duplicate check FAILED: %s", exc)
        sys.exit(2)

    print("\nPipeline summary:")
    print(f"  PDFs processed : {len(cached_pdfs)}")
    print(f"  Parsed rows    : {len(parsed_rows)}")
    print(f"  MarketPrices (Dambulla only):")
    print(f"    Inserted       : {counters.get('inserted', 0)}")
    print(f"    Updated        : {counters.get('updated', 0)}")
    print(f"    Splice-skip    : {counters.get('skipped_splice', 0)}")
    print(f"    No-crop-skip   : {counters.get('skipped_no_crop', 0)}")
    print(f"    Total HARTI DB : {report['grand_total']}")
    print(f"    Duplicate check: PASSED (0 overlaps with DEC)")
    print(f"  PriceObservations (all markets):")
    print(f"    Inserted        : {po_counters.get('inserted', 0)}")
    print(f"    Updated         : {po_counters.get('updated', 0)}")
    print(f"    No-market-skip  : {po_counters.get('skipped_no_market', 0)}")


if __name__ == "__main__":
    main()
