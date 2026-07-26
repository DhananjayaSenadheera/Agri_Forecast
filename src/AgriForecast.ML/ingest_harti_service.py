"""Programmatic HARTI ingestion pass for the /admin/ingest-harti endpoint.

Mirrors ingest_harti.py's main() (the CLI) but takes plain args and RETURNS a
structured summary dict instead of printing/exiting. Used by the FastAPI
/admin/ingest-harti route so the .NET Ingestion Worker can orchestrate the
multi-market HARTI pass over HTTP — exactly as ingest_news.run() backs
/admin/ingest-news.

Design constraints (R1.1 P1 Step 6):
  * The HARTI PDF parser stays the single source of truth here (Python). This
    module reuses downloader/parser/loader — it does not re-implement parsing.
  * PriceObservations is the target table (multi-market). We intentionally run
    only the PriceObservations path here (not the legacy Dambulla-only
    MarketPrices splice), because Step 6 is about the multi-market dimension;
    the full CLI still writes both for back-compat.
  * Data-quality hooks run every pass, closing the recorded follow-ups from
    steps 4-5:
      - assert_no_source_duplicates  -> HARD FAIL (raises) if a cross-source
        duplicate exists; the endpoint turns that into a 502 so the Worker logs
        it and the watermark is NOT advanced.
      - heal_price_observation_crops -> post-pass canonical self-heal (unresolved
        labels stay CropId NULL, never guessed).
      - flag_price_outliers          -> rolling-IQR outlier hold (report only).
      - gap_report                   -> structured gap summary (report only,
        never gates the pass).

Point-in-time discipline is inherited from loader.upsert_harti_price_observations
(AsOfUtc = bulletin publication vintage, never "now").
"""
from __future__ import annotations

import logging
from datetime import date
from pathlib import Path
from typing import Optional

log = logging.getLogger(__name__)

# Same default cache dir the CLI uses.
_DEFAULT_CACHE_DIR = Path(__file__).parent / "harti_cache"

SOURCE = "HARTI"


def run(
    since_date: Optional[str] = None,
    *,
    no_download: bool = False,
    dry_run: bool = False,
    cache_dir: Optional[Path] = None,
) -> dict:
    """Run the multi-market HARTI ingestion pass programmatically.

    Args:
        since_date:  ISO 'YYYY-MM-DD' resume lower bound. Only bulletins with a
                     date strictly AFTER this are downloaded/parsed. None => no
                     lower bound (full backfill). This is the .NET watermark's
                     LastObservedDate, so the daily pass does not re-scrape the
                     whole corpus.
        no_download: Parse from the existing cache only (no network) — used by
                     tests and offline reruns.
        dry_run:     Parse + resolve but do not write to the DB.
        cache_dir:   Override the PDF cache directory (default ./harti_cache).

    Returns:
        Structured summary dict shaped for the .NET IngestHartiResponse:
        {
          "status": "ok",
          "priceObservations": {inserted, updated, skippedNoMarket, maxObservedDate},
          "heal":     {healed, unresolved},
          "outliers": {nFlagged},
          "gaps":     {nInfo, nWarning, nError},
        }

    Raises:
        AssertionError: if the cross-source duplicate check fails (hard-fail).
    """
    from agriforecast_ml.harti import downloader, parser as harti_parser, loader
    from agriforecast_ml.data_quality import (
        assert_no_source_duplicates,
        flag_price_outliers,
        gap_report,
    )
    from agriforecast_ml.canonical import heal_price_observation_crops

    cdir = cache_dir if cache_dir is not None else _DEFAULT_CACHE_DIR
    cdir.mkdir(parents=True, exist_ok=True)

    since: Optional[date] = date.fromisoformat(since_date) if since_date else None

    # Step 1: obtain PDFs (network-bounded by `since`, or cache-only).
    if no_download:
        log.info("ingest_harti_service: no_download — scanning cache at %s", cdir)
        cached_pdfs = []
        for pdf_path in sorted(cdir.glob("harti_*.pdf")):
            date_str = pdf_path.stem.replace("harti_", "")
            try:
                d = date.fromisoformat(date_str)
            except ValueError:
                log.warning("Skipping non-standard filename: %s", pdf_path.name)
                continue
            if since is not None and d <= since:
                continue
            cached_pdfs.append((date_str, pdf_path))
    else:
        import requests

        session = requests.Session()
        log.info("ingest_harti_service: scraping HARTI listing page...")
        links = downloader.scrape_pdf_links(session)
        if since is not None:
            # Resume: only fetch bulletins strictly AFTER the watermark date.
            links = [(d, u) for d, u in links if d > since]
        log.info(
            "ingest_harti_service: %d link(s) to fetch (since=%s)", len(links), since
        )
        cached_pdfs = downloader.download_pdfs(cdir, links, session=session)

    if not cached_pdfs:
        # No new bulletins is a legitimate no-op success (common on the daily
        # incremental pass) — return zero counts, not an error.
        log.info("ingest_harti_service: no new PDFs after since=%s — no-op pass", since)
        return _empty_summary()

    # Step 2: parse (reuse the single-source-of-truth PDF parser).
    parsed_rows = harti_parser.parse_many(cached_pdfs, log_every=200)
    log.info("ingest_harti_service: parsed %d price row(s)", len(parsed_rows))

    # Step 3: upsert into PriceObservations (all markets, point-in-time).
    po_counters = loader.upsert_harti_price_observations(parsed_rows, dry_run=dry_run)
    log.info("ingest_harti_service: PriceObservations upsert %s", po_counters)

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
        "outliers": {"nFlagged": 0},
        "gaps": {"nInfo": 0, "nWarning": 0, "nError": 0},
    }

    if dry_run:
        log.info("ingest_harti_service: dry_run — skipping DB-side QA + heal")
        return summary

    from agriforecast_ml.db import get_engine

    eng = get_engine()

    # Step 4: cross-source duplicate check — HARD FAIL (raises).
    n_checked = assert_no_source_duplicates(eng)
    log.info(
        "ingest_harti_service: duplicate check PASSED (%d triples checked)", n_checked
    )

    # Step 4b: post-pass canonical self-heal (never guesses; unresolved -> NULL).
    heal = heal_price_observation_crops(eng, source=SOURCE)
    summary["heal"] = {
        "healed": heal.get("healed", 0),
        "unresolved": heal.get("unresolved", 0),
    }

    # Step 4c: outlier hold (report only — does not gate the pass).
    outliers = flag_price_outliers(eng, source=SOURCE)
    summary["outliers"] = {"nFlagged": outliers.get("n_flagged", 0)}

    # Step 4d: gap report (report only — never raises for gap findings).
    gaps = gap_report(eng, source=SOURCE)
    summary["gaps"] = {
        "nInfo": gaps.get("n_info", 0),
        "nWarning": gaps.get("n_warning", 0),
        "nError": gaps.get("n_error", 0),
    }

    log.info("ingest_harti_service: pass complete — %s", summary)
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
    """Newest ObservedDate across parsed rows, as 'YYYY-MM-DD' (or None).

    This is the .NET watermark high-water mark. ParsedPrice carries the
    bulletin's date; we read it defensively across a couple of likely attribute
    names so a benign field rename on the parser side degrades to "no watermark
    advance" rather than an exception.
    """
    best: Optional[date] = None
    for r in parsed_rows:
        # ParsedPrice.date_str is the canonical bulletin date ("YYYY-MM-DD").
        d = getattr(r, "date_str", None) or getattr(r, "price_date", None) \
            or getattr(r, "observed_date", None)
        if d is None:
            continue
        if isinstance(d, str):
            try:
                d = date.fromisoformat(d)
            except ValueError:
                continue
        if best is None or d > best:
            best = d
    return best.isoformat() if best is not None else None
