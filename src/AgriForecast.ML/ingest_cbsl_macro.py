"""CBSL macro ingestion pipeline entry point (ClickUp 86cahefbh, P3).

Downloads CCPI press releases + MEI packs from cbsl.gov.lk, parses them
(labeled-line regex, NOT table extraction — see cbsl_macro/parser.py), and
upserts into MacroSeriesPoints keyed on the full (SeriesCode, ReferenceDate,
PublishedAt) vintage triple.

Usage:
    # Full run (scrape both listings, download, parse, upsert):
    python ingest_cbsl_macro.py

    # Parse-only from already-downloaded cache (no network):
    python ingest_cbsl_macro.py --no-download

    # Dry-run: parse + resolve vintages but do NOT write to DB:
    python ingest_cbsl_macro.py --dry-run

Options:
    --cache-dir PATH      Local PDF cache directory (default: ./cbsl_cache)
    --no-download         Skip scraping/downloading; use existing cache only
    --dry-run             Parse and validate but do NOT write to DB
    --log-level LEVEL     Logging verbosity (default: INFO)

"No new bulletin since watermark" is a SUCCESS with zero rows (exit 0), never
an error — this CLI is meant to be safe to run on a schedule (run-monthly.sh,
~15th monthly) where most runs will legitimately find nothing new.
"""
from __future__ import annotations

import argparse
import logging
import sys
from collections import Counter
from datetime import date
from pathlib import Path

from agriforecast_ml.envfile import load_env_file

# Load AGRI_DB_* from the gitignored .env so a bare `python ingest_cbsl_macro.py`
# works without sourcing it first — same pattern build_features.py uses
# (post-PR#16). Real env vars take precedence.
load_env_file()

log = logging.getLogger(__name__)

_DEFAULT_CACHE_DIR = Path(__file__).parent / "cbsl_cache"


def _setup_logging(level: str) -> None:
    logging.basicConfig(
        level=getattr(logging, level.upper(), logging.INFO),
        format="%(asctime)s %(levelname)-8s %(name)s — %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )
    logging.getLogger("pdfminer").setLevel(logging.ERROR)
    logging.getLogger("urllib3").setLevel(logging.WARNING)


def run(
    *,
    cache_dir: Path | None = None,
    no_download: bool = False,
    dry_run: bool = False,
) -> dict:
    """Run the full CBSL macro ingestion pass. Returns a structured summary
    dict (shape mirrors ingest_harti_service.run()'s contract: usable both
    from the CLI's main() and from the /admin/ingest-cbsl-macro FastAPI route).
    """
    from agriforecast_ml.cbsl_macro import downloader, loader, parser as cbsl_parser

    cdir = cache_dir if cache_dir is not None else _DEFAULT_CACHE_DIR
    cdir.mkdir(parents=True, exist_ok=True)

    all_points = []
    artifacts_fetched = 0
    artifacts_skipped = 0

    # ---------------------------------------------------------------- #
    # CCPI press releases
    # ---------------------------------------------------------------- #
    if no_download:
        ccpi_cached = []
        for pdf_path in sorted(cdir.glob("cbsl_ccpi_*.pdf")):
            date_str = pdf_path.stem.replace("cbsl_ccpi_", "")
            try:
                d = date(int(date_str[:4]), int(date_str[4:6]), int(date_str[6:8]))
            except (ValueError, IndexError):
                log.warning("Skipping non-standard CCPI filename: %s", pdf_path.name)
                continue
            digest = downloader.sha256_hex(pdf_path.read_bytes())
            ccpi_cached.append((d, pdf_path, digest))
    else:
        import requests
        session = requests.Session()
        log.info("ingest_cbsl_macro: scraping CCPI listing...")
        ccpi_links = downloader.scrape_ccpi_links(session)
        log.info("ingest_cbsl_macro: %d CCPI link(s) found", len(ccpi_links))
        ccpi_cached = downloader.download_ccpi_pdfs(cdir, ccpi_links, session=session)

    log.info("ingest_cbsl_macro: %d CCPI artifact(s) available in cache", len(ccpi_cached))
    for pub_date, pdf_path, _digest in ccpi_cached:
        points = cbsl_parser.parse_ccpi_pdf(pdf_path, filename_pub_date=pub_date)
        if points:
            artifacts_fetched += 1
        else:
            artifacts_skipped += 1
        all_points.extend(points)

    # ---------------------------------------------------------------- #
    # MEI packs
    # ---------------------------------------------------------------- #
    if no_download:
        mei_cached = []
        for pdf_path in sorted(cdir.glob("cbsl_mei_*.pdf")):
            yyyymm = pdf_path.stem.replace("cbsl_mei_", "")
            if len(yyyymm) != 6 or not yyyymm.isdigit():
                log.warning("Skipping non-standard MEI filename: %s", pdf_path.name)
                continue
            digest = downloader.sha256_hex(pdf_path.read_bytes())
            mei_cached.append((yyyymm, pdf_path, digest))
    else:
        import requests
        session = requests.Session()
        log.info("ingest_cbsl_macro: scraping MEI listing...")
        mei_links = downloader.scrape_mei_links(session)
        log.info("ingest_cbsl_macro: %d MEI link(s) found", len(mei_links))
        mei_cached = downloader.download_mei_pdfs(cdir, mei_links, session=session)

    log.info("ingest_cbsl_macro: %d MEI artifact(s) available in cache", len(mei_cached))
    for pack_yyyymm, pdf_path, _digest in mei_cached:
        points = cbsl_parser.parse_mei_pdf(pdf_path, pack_yyyymm=pack_yyyymm)
        if points:
            artifacts_fetched += 1
        else:
            artifacts_skipped += 1
        all_points.extend(points)

    if not all_points:
        log.info("ingest_cbsl_macro: no parseable macro points this pass — no-op success")
        return {
            "status": "ok",
            "artifactsFetched": artifacts_fetched,
            "artifactsSkipped": artifacts_skipped,
            "rowsInserted": 0,
            "rowsUpdated": 0,
            "rowsSkippedInvalid": 0,
            "perSeriesCoverage": {},
        }

    counters = loader.upsert_macro_points(all_points, dry_run=dry_run)

    coverage = Counter(p.series_code for p in all_points)

    summary = {
        "status": "ok",
        "artifactsFetched": artifacts_fetched,
        "artifactsSkipped": artifacts_skipped,
        "rowsInserted": counters.get("inserted", 0),
        "rowsUpdated": counters.get("updated", 0),
        "rowsSkippedInvalid": counters.get("skipped_invalid", 0),
        "perSeriesCoverage": dict(coverage),
    }
    log.info("ingest_cbsl_macro: pass complete — %s", summary)
    return summary


def main() -> None:
    arg_parser = argparse.ArgumentParser(
        description="CBSL macro ingestion pipeline",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__,
    )
    arg_parser.add_argument(
        "--cache-dir", type=Path, default=_DEFAULT_CACHE_DIR,
        help="Local PDF cache directory (default: ./cbsl_cache)",
    )
    arg_parser.add_argument(
        "--no-download", action="store_true",
        help="Skip scraping/downloading; use existing cache only",
    )
    arg_parser.add_argument(
        "--dry-run", action="store_true",
        help="Parse and validate but do NOT write to DB",
    )
    arg_parser.add_argument(
        "--log-level", default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Logging verbosity (default: INFO)",
    )
    args = arg_parser.parse_args()
    _setup_logging(args.log_level)

    print("=== CBSL macro ingestion ===")
    summary = run(cache_dir=args.cache_dir, no_download=args.no_download, dry_run=args.dry_run)

    print(f"Artifacts fetched (>=1 series parsed): {summary['artifactsFetched']}")
    print(f"Artifacts skipped (0 series parsed):   {summary['artifactsSkipped']}")
    print(f"Rows inserted: {summary['rowsInserted']}")
    print(f"Rows updated:  {summary['rowsUpdated']}")
    print(f"Rows skipped (invalid vintage): {summary['rowsSkippedInvalid']}")
    print("Per-series coverage:")
    for series_code, n in sorted(summary["perSeriesCoverage"].items()):
        print(f"  {series_code}: {n} point(s) parsed")

    sys.exit(0)


if __name__ == "__main__":
    main()
