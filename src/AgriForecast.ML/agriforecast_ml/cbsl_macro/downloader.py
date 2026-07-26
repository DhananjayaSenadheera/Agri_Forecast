"""CBSL macro PDF downloader: CCPI press releases and Monthly Economic Indicators packs.

Mirrors harti/downloader.py (permissive scrape with drop counters, capped streamed
download, polite delay), adapted for two listing pages.

The CCPI listing is a single page with no pagination, and its filenames embed the
publication date as press_YYYYMMDD_..._ccpi_e.pdf.

The MEI listing is Drupal-paginated (?page=N, 12 items per page). Its filenames embed the
pack's YYYYMM, which is NOT the ReferenceDate the pack's trade table describes. Real
filenames vary (MEI_202605_e.pdf, MEI_202503_er.pdf, MEI_202604_e_0.pdf), so the date
regex must tolerate a trailing suffix after the YYYYMM token rather than require an exact
match.

Cache filenames are derived from the PARSED date or month, never the URL basename:
cbsl_ccpi_YYYYMMDD.pdf and cbsl_mei_YYYYMM.pdf.
"""
from __future__ import annotations

import hashlib
import logging
import re
import time
from dataclasses import dataclass
from datetime import date
from pathlib import Path
from urllib.parse import urljoin

import requests
from bs4 import BeautifulSoup

from ..netguard import DisallowedUrlError, assert_url_allowed, guarded_get

logger = logging.getLogger(__name__)

CBSL_BASE = "https://www.cbsl.gov.lk/"
CCPI_LISTING_URL = "https://www.cbsl.gov.lk/en/measures-of-consumer-price-inflation"
MEI_LISTING_URL = "https://www.cbsl.gov.lk/en/statistics/economic-indicators/monthly-indicators"

# CCPI press-release filename date: press_YYYYMMDD_....pdf
_CCPI_DATE_RE = re.compile(r"press_(\d{4})(\d{2})(\d{2})_", re.IGNORECASE)

# MEI pack filename month: MEI_YYYYMM(_suffix)?_e(...).pdf -- tolerant of the
# "_0" Drupal-collision suffix and the "er" (revised) variant seen in the wild.
_MEI_MONTH_RE = re.compile(r"MEI_(\d{4})(\d{2})", re.IGNORECASE)

_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (compatible; AgriForecastBot/1.0; "
        "+https://github.com/AgriForecast)"
    )
}

_REQUEST_DELAY = 1.0            # seconds between requests (polite)
_DOWNLOAD_TIMEOUT = 60           # seconds, per-request
_MAX_PDF_BYTES = 25 * 1024 * 1024  # 25 MB -- same cap as HARTI (F-10 precedent)
_STREAM_CHUNK_BYTES = 64 * 1024
_MAX_LISTING_PAGES = 20          # hard stop on MEI pagination crawl (safety net)


class PdfTooLargeError(Exception):
    """Raised when a downloaded PDF exceeds the configured size cap."""


def _download_capped(session: requests.Session, url: str) -> bytes:
    """Stream ``url`` into memory, aborting if it exceeds ``_MAX_PDF_BYTES``.

    Identical contract to ``harti.downloader._download_capped`` -- see that
    module's docstring for the full SSRF/size-cap rationale. Duplicated here
    (rather than imported) because harti/ and cbsl_macro/ are meant to be
    independently deletable subpackages with no cross-import coupling.
    """
    with guarded_get(
        session, url, headers=_HEADERS, timeout=_DOWNLOAD_TIMEOUT, stream=True
    ) as resp:
        resp.raise_for_status()

        declared = resp.headers.get("Content-Length")
        if declared is not None:
            try:
                if int(declared) > _MAX_PDF_BYTES:
                    raise PdfTooLargeError(
                        f"Content-Length {declared} exceeds cap {_MAX_PDF_BYTES}"
                    )
            except ValueError:
                pass  # malformed header -- rely on the streamed-byte guard below

        buffer = bytearray()
        for chunk in resp.iter_content(chunk_size=_STREAM_CHUNK_BYTES):
            if not chunk:
                continue
            buffer.extend(chunk)
            if len(buffer) > _MAX_PDF_BYTES:
                raise PdfTooLargeError(
                    f"Streamed body exceeded cap {_MAX_PDF_BYTES} bytes"
                )
        return bytes(buffer)


def sha256_hex(data: bytes) -> str:
    """SHA-256 hex digest of a downloaded artifact (recorded per download, spec item 1)."""
    return hashlib.sha256(data).hexdigest()


@dataclass
class CcpiLink:
    pub_date: date          # parsed from the URL filename (press_YYYYMMDD_...)
    url: str


@dataclass
class MeiLink:
    pack_yyyymm: str        # "YYYYMM", parsed from the URL filename
    url: str


def _extract_ccpi_date(href: str) -> date | None:
    m = _CCPI_DATE_RE.search(href)
    if not m:
        return None
    try:
        return date(int(m.group(1)), int(m.group(2)), int(m.group(3)))
    except ValueError:
        return None


def _extract_mei_month(href: str) -> str | None:
    m = _MEI_MONTH_RE.search(href)
    if not m:
        return None
    yyyy, mm = m.group(1), m.group(2)
    try:
        # Validate the month component actually parses as a real month.
        date(int(yyyy), int(mm), 1)
    except ValueError:
        return None
    return f"{yyyy}{mm}"


def scrape_ccpi_links(session: requests.Session) -> list[CcpiLink]:
    """Scrape the CCPI press-release listing page for dated PDF links.

    Permissive scrape-with-drop-counters (harti/downloader.py style): a link
    that is not a CCPI-labelled PDF, or whose filename carries no parseable
    date, is dropped with a counter -- never a hard failure for the whole
    listing. No pagination observed on this listing as of the 2026-07-04
    probe (single page, 18 links back to Jan 2025); if CBSL ever paginates it
    this function would need the same page-walk ``scrape_mei_links`` uses.
    """
    logger.info("Scraping CBSL CCPI listing page: %s", CCPI_LISTING_URL)
    resp = guarded_get(session, CCPI_LISTING_URL, headers=_HEADERS, timeout=30)
    resp.raise_for_status()
    soup = BeautifulSoup(resp.text, "html.parser")

    seen: set[str] = set()
    results: list[CcpiLink] = []
    n_total = n_pdf = n_non_ccpi = n_no_date = n_dup = 0

    for tag in soup.find_all("a", href=True):
        n_total += 1
        href: str = tag["href"]
        if not href.lower().endswith(".pdf"):
            continue
        n_pdf += 1
        if "ccpi" not in href.lower():
            n_non_ccpi += 1
            continue
        d = _extract_ccpi_date(href)
        if d is None:
            n_no_date += 1
            logger.debug("CCPI link with no parseable date, skipping: %s", href)
            continue
        abs_url = urljoin(CBSL_BASE, href)
        if abs_url in seen:
            n_dup += 1
            continue
        seen.add(abs_url)
        results.append(CcpiLink(pub_date=d, url=abs_url))

    logger.info(
        "CCPI scrape complete: %d total links, %d PDFs, %d kept (ccpi+dated) "
        "— dropped: %d non-ccpi, %d no-date, %d duplicate",
        n_total, n_pdf, len(results), n_non_ccpi, n_no_date, n_dup,
    )
    return sorted(results, key=lambda x: x.pub_date)


def scrape_mei_links(
    session: requests.Session,
    *,
    max_pages: int = _MAX_LISTING_PAGES,
) -> list[MeiLink]:
    """Scrape the (paginated) MEI listing page for dated PDF links.

    Walks ``?page=0,1,2,...`` until a page yields zero NEW MEI links (or
    ``max_pages`` is hit, a hard safety stop against a pagination loop/DoS on
    a hostile listing page). Same permissive drop-counter pattern as the CCPI
    scraper and HARTI's ``scrape_pdf_links``.
    """
    seen_urls: set[str] = set()
    results: list[MeiLink] = []
    n_non_mei = n_no_month = n_dup = 0

    for page_no in range(max_pages):
        url = MEI_LISTING_URL if page_no == 0 else f"{MEI_LISTING_URL}?page={page_no}"
        logger.info("Scraping CBSL MEI listing page %d: %s", page_no, url)
        resp = guarded_get(session, url, headers=_HEADERS, timeout=30)
        resp.raise_for_status()
        soup = BeautifulSoup(resp.text, "html.parser")

        page_new = 0
        for tag in soup.find_all("a", href=True):
            href: str = tag["href"]
            if not href.lower().endswith(".pdf"):
                continue
            if "/mei/" not in href.lower():
                n_non_mei += 1
                continue
            month = _extract_mei_month(href)
            if month is None:
                n_no_month += 1
                logger.debug("MEI link with no parseable month, skipping: %s", href)
                continue
            abs_url = urljoin(CBSL_BASE, href)
            if abs_url in seen_urls:
                n_dup += 1
                continue
            seen_urls.add(abs_url)
            results.append(MeiLink(pack_yyyymm=month, url=abs_url))
            page_new += 1

        if page_new == 0:
            logger.info("MEI listing page %d yielded no new links — stopping pagination walk", page_no)
            break
        time.sleep(_REQUEST_DELAY)

    logger.info(
        "MEI scrape complete: %d kept (mei+dated) — dropped: %d non-mei, %d no-month, %d duplicate",
        len(results), n_non_mei, n_no_month, n_dup,
    )
    return sorted(results, key=lambda x: x.pack_yyyymm)


def _safe_download(
    session: requests.Session,
    url: str,
    local_path: Path,
    *,
    label: str,
) -> tuple[bytes, str] | None:
    """Shared download body: SSRF pre-check + capped fetch + sanity size floor.

    Returns (content, sha256_hex) on success, or None on any failure (logged,
    never raised) -- caller increments its own drop counter.
    """
    try:
        assert_url_allowed(url)
    except DisallowedUrlError as exc:
        logger.warning("[%s] Download URL blocked by SSRF allowlist: %s -- %s", label, url, exc)
        return None

    try:
        content = _download_capped(session, url)
        if len(content) < 500:
            logger.warning("[%s] Suspiciously small PDF (%d bytes): %s", label, len(content), url)
            return None
        digest = sha256_hex(content)
        local_path.write_bytes(content)
        logger.info(
            "[%s] Downloaded %.1f KB -> %s (sha256=%s)",
            label, len(content) / 1024, local_path.name, digest,
        )
        return content, digest
    except Exception as exc:
        logger.warning("[%s] Download FAILED: %s -- %s", label, url, exc)
        return None


def _dedup_by_key(links: list, key_fn) -> tuple[list, int]:
    """Keep the FIRST link per logical key and drop later duplicates.

    CBSL's Drupal listing occasionally serves two distinct URLs for the same logical period
    after a re-upload (MEI_201909_e_0.pdf and MEI_201909_e_1.pdf were both live). Deduping by
    URL is not enough: both survive, both get parsed and upserted as the same point, and they
    collide on the (SeriesCode, ReferenceDate, PublishedAt) unique index, aborting the whole
    batch. Deduping by the parsed key up front means at most one artifact per period reaches
    the parser. Dropped duplicates are counted, never silent.
    """
    seen = set()
    kept = []
    n_dropped = 0
    for link in links:
        key = key_fn(link)
        if key in seen:
            n_dropped += 1
            logger.warning(
                "Duplicate artifact for the same period (key=%r) — keeping "
                "the first-seen URL (%s), dropping this one (%s)",
                key, next(l.url for l in kept if key_fn(l) == key), link.url,
            )
            continue
        seen.add(key)
        kept.append(link)
    return kept, n_dropped


def download_ccpi_pdfs(
    cache_dir: Path,
    links: list[CcpiLink],
    session: requests.Session | None = None,
    delay: float = _REQUEST_DELAY,
) -> list[tuple[date, Path, str]]:
    """Download CCPI press-release PDFs. Returns (pub_date, local_path, sha256) for
    all successfully cached files (pre-existing + freshly downloaded).

    Cache filename: cbsl_ccpi_YYYYMMDD.pdf (parsed-date derived, never the URL
    basename — spec requirement, avoids the Drupal duplicate-href/"_0" suffix
    mess leaking into the cache).
    """
    cache_dir.mkdir(parents=True, exist_ok=True)
    if session is None:
        session = requests.Session()

    links, n_dup_period = _dedup_by_key(links, lambda l: l.pub_date)
    if n_dup_period:
        logger.warning("CCPI: dropped %d duplicate-period link(s) before download", n_dup_period)

    cached: list[tuple[date, Path, str]] = []
    n_skip = n_ok = n_fail = 0

    for link in links:
        local_path = cache_dir / f"cbsl_ccpi_{link.pub_date.isoformat().replace('-', '')}.pdf"
        if local_path.exists() and local_path.stat().st_size > 0:
            n_skip += 1
            digest = sha256_hex(local_path.read_bytes())
            cached.append((link.pub_date, local_path, digest))
            continue

        time.sleep(delay)
        result = _safe_download(session, link.url, local_path, label=link.pub_date.isoformat())
        if result is None:
            n_fail += 1
            continue
        _content, digest = result
        n_ok += 1
        cached.append((link.pub_date, local_path, digest))

    logger.info(
        "CCPI download complete: %d downloaded, %d skipped (cached), %d failed",
        n_ok, n_skip, n_fail,
    )
    return cached


def download_mei_pdfs(
    cache_dir: Path,
    links: list[MeiLink],
    session: requests.Session | None = None,
    delay: float = _REQUEST_DELAY,
) -> list[tuple[str, Path, str]]:
    """Download MEI pack PDFs. Returns (pack_yyyymm, local_path, sha256) for all
    successfully cached files.

    Cache filename: cbsl_mei_YYYYMM.pdf (parsed-month derived, never the URL
    basename).
    """
    cache_dir.mkdir(parents=True, exist_ok=True)
    if session is None:
        session = requests.Session()

    links, n_dup_period = _dedup_by_key(links, lambda l: l.pack_yyyymm)
    if n_dup_period:
        logger.warning("MEI: dropped %d duplicate-period link(s) before download", n_dup_period)

    cached: list[tuple[str, Path, str]] = []
    n_skip = n_ok = n_fail = 0

    for link in links:
        local_path = cache_dir / f"cbsl_mei_{link.pack_yyyymm}.pdf"
        if local_path.exists() and local_path.stat().st_size > 0:
            n_skip += 1
            digest = sha256_hex(local_path.read_bytes())
            cached.append((link.pack_yyyymm, local_path, digest))
            continue

        time.sleep(delay)
        result = _safe_download(session, link.url, local_path, label=link.pack_yyyymm)
        if result is None:
            n_fail += 1
            continue
        _content, digest = result
        n_ok += 1
        cached.append((link.pack_yyyymm, local_path, digest))

    logger.info(
        "MEI download complete: %d downloaded, %d skipped (cached), %d failed",
        n_ok, n_skip, n_fail,
    )
    return cached
