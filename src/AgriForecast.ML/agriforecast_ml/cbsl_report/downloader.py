"""CBSL Daily Price Report PDF downloader.

Corpus-probed 2026-07-22 (see the feat/cbsl-price-parser PR): the report URL is
DETERMINISTIC per date — no listing page to scrape (unlike HARTI):

    https://www.cbsl.gov.lk/sites/default/files/cbslweb_documents/statistics/
        pricerpt/price_report_{YYYYMMDD}_e.pdf

Verified 200s on weekdays across 2024/2025/2026; weekends and public holidays
return 404 because NO report is published — a 404 here is therefore a NORMAL
calendar gap, never a source failure. Anything other than 200/404 is WARN-
logged and skipped (the next pass retries it — the upsert is idempotent).

Cache convention mirrors harti/downloader.py: ``cbsl_{YYYY-MM-DD}.pdf`` in the
cache dir; an already-cached date is never re-downloaded (the report for a
given date is immutable once published).
"""
from __future__ import annotations

import logging
from datetime import date, timedelta
from pathlib import Path
from typing import Iterable

log = logging.getLogger(__name__)

URL_TEMPLATE = (
    "https://www.cbsl.gov.lk/sites/default/files/cbslweb_documents/"
    "statistics/pricerpt/price_report_{yyyymmdd}_e.pdf"
)

# Belt against a mis-set watermark asking for a giant range: the daily pass is incremental
# and the capture-only scope has no historical backfill. A caller that genuinely wants one
# passes an explicit wide range and raises this cap deliberately.
DEFAULT_MAX_DATES_PER_PASS = 62


def candidate_dates(since: date | None, until: date) -> list[date]:
    """Dates to try, ascending: strictly AFTER ``since`` up to ``until``.

    ``since=None`` (empty watermark, first run) => the last 7 calendar days
    only — the capture-only contract: no silent full backfill on first run.
    Weekends are NOT pre-filtered: holidays 404 anyway, so the 404-is-normal
    rule in download_pdfs() handles both uniformly (simpler + honest).
    """
    if since is None:
        since = until - timedelta(days=7)
    days = (until - since).days
    if days <= 0:
        return []
    return [since + timedelta(days=i) for i in range(1, days + 1)]


def pdf_url(d: date) -> str:
    return URL_TEMPLATE.format(yyyymmdd=d.strftime("%Y%m%d"))


def download_pdfs(
    cache_dir: Path,
    dates: Iterable[date],
    *,
    session=None,
    max_dates: int = DEFAULT_MAX_DATES_PER_PASS,
) -> list[tuple[str, Path]]:
    """Fetch each date's report into the cache; return [(date_str, path)].

    Returns cached-or-downloaded PDFs only — 404 dates (weekend/holiday: no
    report published) are silently absent from the result; non-404 failures
    are WARN-logged and absent (retried naturally next pass).
    """
    import requests

    sess = session or requests.Session()
    dates = list(dates)
    if len(dates) > max_dates:
        log.warning(
            "CBSL downloader: %d candidate dates exceeds the per-pass cap %d — "
            "truncating to the OLDEST %d so the watermark still advances in "
            "order (no silent full backfill; raise max_dates deliberately for "
            "a real backfill).",
            len(dates), max_dates, max_dates,
        )
        dates = dates[:max_dates]

    out: list[tuple[str, Path]] = []
    for d in dates:
        date_str = d.isoformat()
        path = cache_dir / f"cbsl_{date_str}.pdf"
        if path.exists():
            out.append((date_str, path))
            continue
        url = pdf_url(d)
        try:
            resp = sess.get(url, timeout=60)
        except Exception:
            log.warning("CBSL downloader: fetch failed for %s (%s) — will retry next pass",
                        date_str, url, exc_info=True)
            continue
        if resp.status_code == 404:
            # Normal calendar gap: no report is published on weekends/holidays.
            log.info("CBSL downloader: no report published for %s (404) — normal gap", date_str)
            continue
        if resp.status_code != 200:
            log.warning("CBSL downloader: HTTP %d for %s (%s) — will retry next pass",
                        resp.status_code, date_str, url)
            continue
        path.write_bytes(resp.content)
        out.append((date_str, path))
        log.info("CBSL downloader: fetched %s (%d bytes)", date_str, len(resp.content))
    return out
