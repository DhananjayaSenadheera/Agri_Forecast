"""CBSL macro ingestion pipeline entry point (ClickUp 86cahefbh, P3).

Downloads CCPI press releases + MEI packs from cbsl.gov.lk, parses them
(labeled-line regex, NOT table extraction — see cbsl_macro/parser.py), and
upserts into MacroSeriesPoints keyed on the full (SeriesCode, ReferenceDate,
PublishedAt) vintage triple.

Usage:
    # Incremental run (DB-watermark-driven; scrape both listings, but only
    # download/parse artifacts the watermark says could be new):
    python ingest_cbsl_macro.py

    # Parse-only from already-downloaded cache (no network), still
    # watermark-filtered:
    python ingest_cbsl_macro.py --no-download

    # Dry-run: parse + resolve vintages but do NOT write to DB:
    python ingest_cbsl_macro.py --dry-run

    # Force a full re-scan of the entire corpus, ignoring the watermark
    # (backfill / repair; the upsert is idempotent so this is always safe,
    # just slower):
    python ingest_cbsl_macro.py --full

Options:
    --cache-dir PATH      Local PDF cache directory (default: ./cbsl_cache)
    --no-download         Skip scraping/downloading; use existing cache only
    --dry-run             Parse and validate but do NOT write to DB
    --full                Ignore the DB watermark and re-scan every artifact
    --log-level LEVEL     Logging verbosity (default: INFO)

INCREMENTAL DESIGN (2026-08 fix — see WRITE-BACK for the full incident):
this CLI used to have NO watermark despite its docstring's earlier claim,
scraping/parsing/downloading the FULL CCPI+MEI corpus (all ~65 artifacts,
2020-04→present and growing every month) on every single run, accumulating
every parsed point into one list before a single upsert call. That OOMKilled
the k8s CronJob (768Mi limit) on its first-ever run and would only get worse
as the corpus grows. Now:

  1. Before touching the network, `loader.get_watermarks()` reads the
     highest PublishedAt (CCPI) / inferred pack month (MEI) already in
     MacroSeriesPoints, and `downloader.filter_*_since()` drops any listed
     artifact more than 1 calendar month older than that watermark — BEFORE
     it is downloaded, so an ephemeral k8s cache never forces a full
     re-download. The 1-month overlap is a safety margin, never a
     correctness requirement: re-parsing an already-ingested artifact is a
     harmless no-op (the upsert is idempotent on the full vintage triple).
  2. Each artifact is parsed AND upserted immediately, one at a time — never
     accumulated into one big list first — so peak memory stays O(one
     artifact's points) regardless of corpus size, including a true
     `--full` backfill against an empty DB.

"No new bulletin since watermark" is a SUCCESS with zero rows (exit 0), never
an error — this CLI is meant to be safe to run on a schedule (the k8s
monthly-cbsl-macro CronJob) where most runs will legitimately find nothing
new.

HONEST FAILURE REPORTING (2026-08-17 reviewer fix): a DB-write failure on one
artifact (per-artifact isolation, see design note 2 above) is now counted
separately in the summary's `artifactsFailed` key and flips `status` to
"partial" — it is NEVER folded into "0 series parsed" `artifactsSkipped`,
and it is never silently reported as `status="ok"` / exit 0 the way it was
before this fix (a real regression vs. the pre-incremental main(), which let
the exception propagate loudly). `status != "ok"` makes `main()` exit 1, so
k8s marks the Job Failed. "No new bulletin" always has `artifactsFailed == 0`
and is unaffected — it still exits 0.
"""
from __future__ import annotations

import argparse
import logging
import sys
from collections import Counter
from datetime import date
from pathlib import Path

from agriforecast_ml.envfile import load_env_file

# Load AGRI_DB_* from the gitignored .env so a bare 'python ingest_cbsl_macro.py' works
# without sourcing it first. Real environment variables take precedence.
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
    full: bool = False,
) -> dict:
    """Run the full CBSL macro ingestion pass. Returns a structured summary
    dict (shape mirrors ingest_harti_service.run()'s contract: usable both
    from the CLI's main() and from the /admin/ingest-cbsl-macro FastAPI route).

    Args:
        cache_dir:   Override the PDF cache directory (default ./cbsl_cache).
        no_download: Parse from the existing cache only (no network).
        dry_run:     Parse + resolve vintages but do not write to the DB.
        full:        Ignore the DB watermark and re-scan/re-parse the entire
                     corpus (backfill / repair). Default OFF — the normal
                     scheduled pass is watermark-driven.
    """
    from agriforecast_ml.cbsl_macro import downloader, loader, parser as cbsl_parser

    cdir = cache_dir if cache_dir is not None else _DEFAULT_CACHE_DIR
    cdir.mkdir(parents=True, exist_ok=True)

    if full:
        watermarks = loader.MacroWatermarks(ccpi_published_at=None, mei_pack_yyyymm=None)
        log.info("ingest_cbsl_macro: --full requested — watermark skip DISABLED, full re-scan")
    else:
        watermarks = loader.get_watermarks()
        log.info(
            "ingest_cbsl_macro: DB watermarks — CCPI PublishedAt>=%s, MEI pack>=%s "
            "(minus a %d-month safety overlap, applied at filter time)",
            watermarks.ccpi_published_at, watermarks.mei_pack_yyyymm,
            downloader._WATERMARK_OVERLAP_MONTHS,
        )
        # A source with no prior coverage at all on a non-full run means this
        # pass will scan that source's FULL listing (same cost as --full for
        # that source alone) -- cheap to flag, easy to miss otherwise.
        if watermarks.ccpi_published_at is None:
            log.warning("ingest_cbsl_macro: no prior CCPI coverage in the DB — this pass scans the FULL CCPI listing")
        if watermarks.mei_pack_yyyymm is None:
            log.warning("ingest_cbsl_macro: no prior MEI coverage in the DB — this pass scans the FULL MEI listing")

    artifacts_fetched = 0
    artifacts_skipped = 0
    artifacts_failed = 0
    artifacts_watermark_skipped = 0
    rows_inserted = 0
    rows_updated = 0
    rows_skipped_invalid = 0
    coverage: Counter = Counter()

    # --- CCPI press releases -------------------------------------------------
    if no_download:
        ccpi_raw = []
        for pdf_path in sorted(cdir.glob("cbsl_ccpi_*.pdf")):
            date_str = pdf_path.stem.replace("cbsl_ccpi_", "")
            try:
                d = date(int(date_str[:4]), int(date_str[4:6]), int(date_str[6:8]))
            except (ValueError, IndexError):
                log.warning("Skipping non-standard CCPI filename: %s", pdf_path.name)
                continue
            ccpi_raw.append((d, pdf_path))
        ccpi_raw, n_ccpi_wm_skipped = downloader.filter_ccpi_since(
            ccpi_raw, lambda t: t[0], watermarks.ccpi_published_at
        )
        ccpi_cached = [(d, p, downloader.sha256_hex(p.read_bytes())) for d, p in ccpi_raw]
    else:
        import requests
        session = requests.Session()
        log.info("ingest_cbsl_macro: scraping CCPI listing...")
        ccpi_links = downloader.scrape_ccpi_links(session)
        log.info("ingest_cbsl_macro: %d CCPI link(s) found", len(ccpi_links))
        ccpi_links, n_ccpi_wm_skipped = downloader.filter_ccpi_since(
            ccpi_links, lambda l: l.pub_date, watermarks.ccpi_published_at
        )
        ccpi_cached = downloader.download_ccpi_pdfs(cdir, ccpi_links, session=session)
    artifacts_watermark_skipped += n_ccpi_wm_skipped

    log.info(
        "ingest_cbsl_macro: %d CCPI artifact(s) to parse (%d dropped by the watermark)",
        len(ccpi_cached), n_ccpi_wm_skipped,
    )
    for pub_date, pdf_path, _digest in ccpi_cached:
        points = cbsl_parser.parse_ccpi_pdf(pdf_path, filename_pub_date=pub_date)
        if not points:
            artifacts_skipped += 1
            continue
        try:
            counters = loader.upsert_macro_points(points, dry_run=dry_run)
        except Exception:
            # Per-artifact isolation: one bad artifact's DB write must never
            # abort the rest of the pass — the old single-big-transaction
            # shape would have lost EVERY other artifact's rows too. This is
            # a genuine FAILURE though (not a "0 series parsed" skip), so it
            # is counted and reported separately — see artifactsFailed below.
            log.exception(
                "ingest_cbsl_macro: upsert FAILED for CCPI artifact %s — this "
                "artifact's rows are LOST, the rest of the pass continues",
                pdf_path.name,
            )
            artifacts_failed += 1
            continue
        artifacts_fetched += 1
        coverage.update(p.series_code for p in points)
        rows_inserted += counters.get("inserted", 0)
        rows_updated += counters.get("updated", 0)
        rows_skipped_invalid += counters.get("skipped_invalid", 0)

    # --- MEI packs -------------------------------------------------------------
    if no_download:
        mei_raw = []
        for pdf_path in sorted(cdir.glob("cbsl_mei_*.pdf")):
            yyyymm = pdf_path.stem.replace("cbsl_mei_", "")
            if len(yyyymm) != 6 or not yyyymm.isdigit():
                log.warning("Skipping non-standard MEI filename: %s", pdf_path.name)
                continue
            mei_raw.append((yyyymm, pdf_path))
        mei_raw, n_mei_wm_skipped = downloader.filter_mei_since(
            mei_raw, lambda t: t[0], watermarks.mei_pack_yyyymm
        )
        mei_cached = [(y, p, downloader.sha256_hex(p.read_bytes())) for y, p in mei_raw]
    else:
        import requests
        session = requests.Session()
        log.info("ingest_cbsl_macro: scraping MEI listing...")
        mei_links = downloader.scrape_mei_links(session)
        log.info("ingest_cbsl_macro: %d MEI link(s) found", len(mei_links))
        mei_links, n_mei_wm_skipped = downloader.filter_mei_since(
            mei_links, lambda l: l.pack_yyyymm, watermarks.mei_pack_yyyymm
        )
        mei_cached = downloader.download_mei_pdfs(cdir, mei_links, session=session)
    artifacts_watermark_skipped += n_mei_wm_skipped

    log.info(
        "ingest_cbsl_macro: %d MEI artifact(s) to parse (%d dropped by the watermark)",
        len(mei_cached), n_mei_wm_skipped,
    )
    for pack_yyyymm, pdf_path, _digest in mei_cached:
        points = cbsl_parser.parse_mei_pdf(pdf_path, pack_yyyymm=pack_yyyymm)
        if not points:
            artifacts_skipped += 1
            continue
        try:
            counters = loader.upsert_macro_points(points, dry_run=dry_run)
        except Exception:
            log.exception(
                "ingest_cbsl_macro: upsert FAILED for MEI artifact %s — this "
                "artifact's rows are LOST, the rest of the pass continues",
                pdf_path.name,
            )
            artifacts_failed += 1
            continue
        artifacts_fetched += 1
        coverage.update(p.series_code for p in points)
        rows_inserted += counters.get("inserted", 0)
        rows_updated += counters.get("updated", 0)
        rows_skipped_invalid += counters.get("skipped_invalid", 0)

    # status is "ok" only when every fetched artifact's rows actually landed.
    # A DB-write failure is a real loss of data, not merely "found nothing
    # new" — it must never be reported the same way as a legitimate
    # zero-new-artifacts pass (both used to report status="ok"/exit 0, which
    # is the honest-reporting regression this fixes). "partial" because
    # per-artifact isolation means the OTHER artifacts in the same pass still
    # succeeded — this is not a whole-pipeline failure.
    status = "partial" if artifacts_failed > 0 else "ok"

    summary = {
        "status": status,
        "artifactsFetched": artifacts_fetched,
        "artifactsSkipped": artifacts_skipped,
        # Distinct from artifactsSkipped ("0 series parsed", a benign PDF
        # content miss) — a DB upsert EXCEPTION on an otherwise successfully
        # parsed artifact, whose rows are consequently lost this pass.
        "artifactsFailed": artifacts_failed,
        # Additive field (not part of the original contract): artifacts the
        # watermark dropped BEFORE download/parse. Zero on a true backfill or
        # --full run; high on a routine steady-state monthly pass — the
        # number that proves this fix is actually working in production.
        "artifactsWatermarkSkipped": artifacts_watermark_skipped,
        "rowsInserted": rows_inserted,
        "rowsUpdated": rows_updated,
        "rowsSkippedInvalid": rows_skipped_invalid,
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
        "--full", action="store_true",
        help="Ignore the DB watermark and re-scan/re-parse the entire corpus "
             "(backfill / repair). Default OFF.",
    )
    arg_parser.add_argument(
        "--log-level", default="INFO",
        choices=["DEBUG", "INFO", "WARNING", "ERROR"],
        help="Logging verbosity (default: INFO)",
    )
    args = arg_parser.parse_args()
    _setup_logging(args.log_level)

    print("=== CBSL macro ingestion ===")
    summary = run(
        cache_dir=args.cache_dir,
        no_download=args.no_download,
        dry_run=args.dry_run,
        full=args.full,
    )

    print(f"Status: {summary['status']}")
    print(f"Artifacts fetched (>=1 series parsed):    {summary['artifactsFetched']}")
    print(f"Artifacts skipped (0 series parsed):      {summary['artifactsSkipped']}")
    print(f"Artifacts FAILED (DB upsert error):       {summary['artifactsFailed']}")
    print(f"Artifacts skipped (before watermark):     {summary['artifactsWatermarkSkipped']}")
    print(f"Rows inserted: {summary['rowsInserted']}")
    print(f"Rows updated:  {summary['rowsUpdated']}")
    print(f"Rows skipped (invalid vintage): {summary['rowsSkippedInvalid']}")
    print("Per-series coverage:")
    for series_code, n in sorted(summary["perSeriesCoverage"].items()):
        print(f"  {series_code}: {n} point(s) parsed")

    if summary["status"] != "ok":
        # A real DB-write failure occurred this pass (artifactsFailed > 0) --
        # exit non-zero so k8s marks the Job Failed and it shows up loudly,
        # instead of the old silent "status ok / exit 0" regression. "No new
        # bulletin since watermark" is untouched: that path always has
        # artifactsFailed == 0, so status stays "ok" and this still exits 0.
        print(f"ingest_cbsl_macro: FAILED — {summary['artifactsFailed']} artifact(s) lost a DB write this pass", file=sys.stderr)
        sys.exit(1)

    sys.exit(0)


if __name__ == "__main__":
    main()
