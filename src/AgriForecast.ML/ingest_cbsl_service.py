"""Programmatic CBSL Daily Price Report pass for /admin/ingest-cbsl.

Mirrors ingest_harti_service.run() step-for-step (download -> parse -> upsert
-> DB-side QA), returning the SAME summary shape so the .NET side reuses one
response contract. Differences, both deliberate:

  * No listing scrape: the report URL is deterministic per date, so the
    downloader probes (sinceDate, today] directly. A 404 = no report published
    (weekend/holiday) = a normal gap. An EMPTY sinceDate (first run) fetches
    only the last 7 days — capture-only scope (owner 2026-07-22), no silent
    backfill.
  * flag_price_outliers is NOT run for CBSL: it scans MinPrice/MaxPrice
    midpoints and CBSL rows carry point figures in WholesalePrice/RetailPrice
    with Min/Max NULL (see cbsl_report/loader.py), so the scan cannot see them
    — reporting its 0 as "checked clean" would be a false claim. The summary's
    outliers block therefore honestly reports 0 flagged with nothing scanned.

assert_no_source_duplicates stays a HARD FAIL (raises -> the route's 502): the
CBSL columns write into markets other sources also cover, exactly the overlap
_ADJUDICATED_OVERLAPS adjudicates — anything outside it must stop the pass.
"""
from __future__ import annotations

import logging
from datetime import date
from pathlib import Path
from typing import Optional

log = logging.getLogger(__name__)

_DEFAULT_CACHE_DIR = Path(__file__).parent / "cbsl_cache"

SOURCE = "CBSL"


def run(
    since_date: Optional[str] = None,
    *,
    no_download: bool = False,
    dry_run: bool = False,
    cache_dir: Optional[Path] = None,
) -> dict:
    """Run the CBSL daily-report ingestion pass programmatically.

    Args mirror ingest_harti_service.run(); returns the same summary dict
    shape. Raises AssertionError on a cross-source duplicate violation.
    """
    from agriforecast_ml.cbsl_report import downloader, loader
    from agriforecast_ml.cbsl_report import parser as cbsl_parser
    from agriforecast_ml.canonical import heal_price_observation_crops
    from agriforecast_ml.data_quality import assert_no_source_duplicates, gap_report

    cdir = cache_dir if cache_dir is not None else _DEFAULT_CACHE_DIR
    cdir.mkdir(parents=True, exist_ok=True)

    since: Optional[date] = date.fromisoformat(since_date) if since_date else None

    if no_download:
        log.info("ingest_cbsl_service: no_download — scanning cache at %s", cdir)
        cached_pdfs = []
        for pdf_path in sorted(cdir.glob("cbsl_*.pdf")):
            date_str = pdf_path.stem.replace("cbsl_", "")
            try:
                d = date.fromisoformat(date_str)
            except ValueError:
                log.warning("Skipping non-standard filename: %s", pdf_path.name)
                continue
            if since is not None and d <= since:
                continue
            cached_pdfs.append((date_str, pdf_path))
    else:
        dates = downloader.candidate_dates(since, date.today())
        log.info("ingest_cbsl_service: %d candidate date(s) to probe (since=%s)",
                 len(dates), since)
        cached_pdfs = downloader.download_pdfs(cdir, dates)

    if not cached_pdfs:
        log.info("ingest_cbsl_service: no new reports after since=%s — no-op pass", since)
        return _empty_summary()

    parsed_rows = cbsl_parser.parse_many(cached_pdfs)
    log.info("ingest_cbsl_service: parsed %d today-price row(s)", len(parsed_rows))

    po_counters = loader.upsert_cbsl_price_observations(parsed_rows, dry_run=dry_run)
    log.info("ingest_cbsl_service: PriceObservations upsert %s", po_counters)

    max_observed = _max_observed_date(parsed_rows)

    summary = {
        "status": "ok",
        "priceObservations": {
            "inserted": po_counters.get("inserted", 0),
            "updated": po_counters.get("updated", 0),
            "skippedNoMarket": po_counters.get("skipped_no_market", 0),
            "maxObservedDate": max_observed,
        },
        "heal": {"healed": 0, "unresolved": 0},
        "outliers": {"nFlagged": 0},  # not scanned for CBSL — see module doc
        "gaps": {"nInfo": 0, "nWarning": 0, "nError": 0},
    }

    if dry_run:
        log.info("ingest_cbsl_service: dry_run — skipping DB-side QA + heal")
        return summary

    from agriforecast_ml.db import get_engine

    eng = get_engine()

    # Cross-source duplicate check — HARD FAIL (raises -> route 502).
    n_checked = assert_no_source_duplicates(eng)
    log.info("ingest_cbsl_service: duplicate check PASSED (%d triples checked)", n_checked)

    heal = heal_price_observation_crops(eng, source=SOURCE)
    summary["heal"] = {
        "healed": heal.get("healed", 0),
        "unresolved": heal.get("unresolved", 0),
    }

    gaps = gap_report(eng, source=SOURCE)
    summary["gaps"] = {
        "nInfo": gaps.get("n_info", 0),
        "nWarning": gaps.get("n_warning", 0),
        "nError": gaps.get("n_error", 0),
    }

    log.info("ingest_cbsl_service: pass complete — %s", summary)
    return summary


def _empty_summary() -> dict:
    return {
        "status": "ok",
        "priceObservations": {
            "inserted": 0,
            "updated": 0,
            "skippedNoMarket": 0,
            "maxObservedDate": None,
        },
        "heal": {"healed": 0, "unresolved": 0},
        "outliers": {"nFlagged": 0},
        "gaps": {"nInfo": 0, "nWarning": 0, "nError": 0},
    }


def _max_observed_date(parsed_rows) -> Optional[str]:
    """Newest report date across parsed rows ('YYYY-MM-DD'), the .NET watermark."""
    best: Optional[date] = None
    for r in parsed_rows:
        d = getattr(r, "date_str", None)
        if d is None:
            continue
        try:
            d = date.fromisoformat(d)
        except ValueError:
            continue
        if best is None or d > best:
            best = d
    return best.isoformat() if best is not None else None
