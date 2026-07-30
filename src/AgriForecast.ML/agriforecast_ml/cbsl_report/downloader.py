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

Every fetch goes through netguard.guarded_get (host allowlist + private-IP block
re-checked on the initial URL AND on every redirect hop) and is streamed under a
25 MB cap, the same choke point harti/ and cbsl_macro/ use.
"""
from __future__ import annotations

import logging
from datetime import date, timedelta
from pathlib import Path
from typing import Iterable

import requests

from ..netguard import DisallowedUrlError, guarded_get

log = logging.getLogger(__name__)

URL_TEMPLATE = (
    "https://www.cbsl.gov.lk/sites/default/files/cbslweb_documents/"
    "statistics/pricerpt/price_report_{yyyymmdd}_e.pdf"
)

# Belt against a mis-set watermark asking for a giant range: the daily pass is incremental
# and the capture-only scope has no historical backfill. A caller that genuinely wants one
# passes an explicit wide range and raises this cap deliberately.
DEFAULT_MAX_DATES_PER_PASS = 62

_DOWNLOAD_TIMEOUT = 60              # seconds, per-request
_MAX_PDF_BYTES = 25 * 1024 * 1024   # 25 MB — same cap as HARTI and cbsl_macro
_STREAM_CHUNK_BYTES = 64 * 1024     # 64 KB read chunks


class PdfTooLargeError(Exception):
    """Raised when a downloaded PDF exceeds the configured size cap."""


def _fetch_capped(session: requests.Session, url: str) -> tuple[int, bytes]:
    """Fetch ``url`` through the SSRF guard, streaming the body under the cap.

    Returns (status_code, content). Non-200 responses return EMPTY content and
    their body is never read, so the 404-is-a-normal-gap branch in
    download_pdfs() stays as cheap and as non-error as it was before.

    Same size-cap contract as ``harti.downloader._download_capped``: the
    Content-Length header is a cheap pre-check and the streamed byte count is
    the real guard, because the header can be absent, wrong or hostile.
    Duplicated rather than imported to keep the source subpackages independently
    deletable (the reason cbsl_macro duplicates it too). ``raise_for_status`` is
    deliberately NOT called here — this caller classifies status codes itself.
    """
    with guarded_get(session, url, timeout=_DOWNLOAD_TIMEOUT, stream=True) as resp:
        if resp.status_code != 200:
            return resp.status_code, b""

        declared = resp.headers.get("Content-Length")
        if declared is not None:
            try:
                if int(declared) > _MAX_PDF_BYTES:
                    raise PdfTooLargeError(
                        f"Content-Length {declared} exceeds cap {_MAX_PDF_BYTES}"
                    )
            except ValueError:
                pass  # malformed header — rely on the streamed-byte guard below

        buffer = bytearray()
        for chunk in resp.iter_content(chunk_size=_STREAM_CHUNK_BYTES):
            if not chunk:
                continue
            buffer.extend(chunk)
            if len(buffer) > _MAX_PDF_BYTES:
                raise PdfTooLargeError(
                    f"Streamed body exceeded cap {_MAX_PDF_BYTES} bytes"
                )
        return 200, bytes(buffer)


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
            status, content = _fetch_capped(sess, url)
        except DisallowedUrlError as exc:
            # The URL itself, or a redirect hop, left the host allowlist / hit the
            # private-IP block. Skip this date only — one bad hop must not end the pass.
            log.warning("CBSL downloader: URL blocked by SSRF guard for %s (%s) — %s",
                        date_str, url, exc)
            continue
        except PdfTooLargeError as exc:
            log.warning("CBSL downloader: oversized response for %s (%s) — %s — skipping",
                        date_str, url, exc)
            continue
        except Exception:
            log.warning("CBSL downloader: fetch failed for %s (%s) — will retry next pass",
                        date_str, url, exc_info=True)
            continue
        if status == 404:
            # Normal calendar gap: no report is published on weekends/holidays.
            log.info("CBSL downloader: no report published for %s (404) — normal gap", date_str)
            continue
        if status != 200:
            log.warning("CBSL downloader: HTTP %d for %s (%s) — will retry next pass",
                        status, date_str, url)
            continue
        path.write_bytes(content)
        out.append((date_str, path))
        log.info("CBSL downloader: fetched %s (%d bytes)", date_str, len(content))
    return out
