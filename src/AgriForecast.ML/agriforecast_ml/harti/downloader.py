"""HARTI PDF downloader.

Scrapes https://www.harti.gov.lk/daily-price.php for all PDF links
(DO NOT pattern-generate — two filename patterns + variants exist), then
downloads each to a local cache directory.

Behaviour:
- Polite: ~1 req/s inter-request delay.
- Resumable / idempotent: already-downloaded files are skipped.
- Logs failures; never raises on individual download error.
- Returns the list of (date_str, local_path) for successfully cached PDFs.

Filename patterns observed in the wild:
  Pattern A (2023+): "Vegetable Pricenew ex1(YYYY.MM.DD).pdf"
                     "Vegetables Wholesale Prices (YYYY.MM.DD).pdf"
                     "Vegetabale Wholesale Prices (YYYY.MM.DD).pdf"  [sic]
  Pattern B (2015-2022): "daily_DD-MM-YYYY.pdf"
"""
from __future__ import annotations

import re
import time
import logging
from datetime import date
from pathlib import Path
from urllib.parse import quote, urljoin

import requests
from bs4 import BeautifulSoup

logger = logging.getLogger(__name__)

HARTI_BASE = "https://www.harti.gov.lk/"
LISTING_URL = "https://www.harti.gov.lk/daily-price.php"

# Pattern A: date in parentheses as YYYY.MM.DD
#   "Vegetable Pricenew ex1(2023.01.15).pdf"
_DATE_RE_A = re.compile(r"\((\d{4})\.(\d{2})\.(\d{2})\)")

# Pattern B: "daily_DD-MM-YYYY.pdf"
_DATE_RE_B = re.compile(r"daily_(\d{2})-(\d{2})-(\d{4})\.pdf", re.IGNORECASE)

# User-agent so we look like a browser
_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (compatible; AgriForecastBot/1.0; "
        "+https://github.com/AgriForecast)"
    )
}

_REQUEST_DELAY = 1.0   # seconds between requests


def _parse_date_from_filename(filename: str) -> date | None:
    """Extract date from HARTI PDF filename.  Returns None if no match.

    Handles:
      Pattern A (2023+): anything embedding (YYYY.MM.DD)
      Pattern B (2015-2022): daily_DD-MM-YYYY.pdf
    """
    # Pattern A
    m = _DATE_RE_A.search(filename)
    if m:
        try:
            return date(int(m.group(1)), int(m.group(2)), int(m.group(3)))
        except ValueError:
            return None

    # Pattern B
    m = _DATE_RE_B.search(filename)
    if m:
        try:
            # Groups: (DD, MM, YYYY)
            return date(int(m.group(3)), int(m.group(2)), int(m.group(1)))
        except ValueError:
            return None

    return None


def scrape_pdf_links(session: requests.Session) -> list[tuple[date, str]]:
    """Return [(date, relative_url), ...] for all vegetable-price PDFs on the
    HARTI listing page.  Only English PDFs in the /eng/ path are collected."""
    logger.info("Scraping HARTI listing page: %s", LISTING_URL)
    resp = session.get(LISTING_URL, headers=_HEADERS, timeout=30)
    resp.raise_for_status()
    soup = BeautifulSoup(resp.text, "html.parser")

    seen: set[str] = set()
    results: list[tuple[date, str]] = []

    n_total_links = 0      # all <a href> tags on the page
    n_pdf_links = 0        # .pdf links found before /eng/ filter
    n_non_eng = 0          # dropped by /eng/ path filter
    n_no_date = 0          # dropped because filename has no parseable date
    n_dup = 0              # dropped as duplicates

    for tag in soup.find_all("a", href=True):
        n_total_links += 1
        href: str = tag["href"]
        if not href.lower().endswith(".pdf"):
            continue
        n_pdf_links += 1
        # Keep only English vegetable PDFs under assets/pdf/food_price/daily/eng/
        if "/eng/" not in href:
            n_non_eng += 1
            logger.debug("Non-/eng/ PDF link dropped: %s", href)
            continue
        # Normalise: strip leading slash or domain if present
        href = href.lstrip("/")
        if href.startswith("http"):
            # absolute url — extract path portion
            from urllib.parse import urlparse
            href = urlparse(href).path.lstrip("/")

        d = _parse_date_from_filename(href)
        if d is None:
            n_no_date += 1
            logger.debug("No date in link, skipping: %s", href)
            continue
        if href in seen:
            n_dup += 1
            continue
        seen.add(href)
        results.append((d, href))

    logger.info(
        "Scrape complete: %d total links, %d PDFs seen, %d kept (/eng/ dated unique) "
        "— dropped: %d non-/eng/, %d no-date, %d duplicate",
        n_total_links, n_pdf_links, len(results),
        n_non_eng, n_no_date, n_dup,
    )
    return sorted(results, key=lambda x: x[0])


def download_pdfs(
    cache_dir: Path,
    links: list[tuple[date, str]],
    session: requests.Session | None = None,
    delay: float = _REQUEST_DELAY,
) -> list[tuple[str, Path]]:
    """Download PDFs to cache_dir.  Skips already-cached files.

    Args:
        cache_dir:  Local directory; created if it doesn't exist.
        links:      Output of scrape_pdf_links().
        session:    Optional requests.Session (created internally if None).
        delay:      Seconds to sleep between actual downloads (polite crawl).

    Returns:
        List of (date_str "YYYY-MM-DD", local_path) for ALL successfully cached
        PDFs (both pre-existing and freshly downloaded).
    """
    cache_dir.mkdir(parents=True, exist_ok=True)
    if session is None:
        session = requests.Session()

    cached: list[tuple[str, Path]] = []
    n_skip = n_ok = n_fail = 0

    for d, rel_url in links:
        date_str = d.isoformat()
        local_path = cache_dir / f"harti_{date_str}.pdf"

        if local_path.exists() and local_path.stat().st_size > 0:
            n_skip += 1
            cached.append((date_str, local_path))
            continue

        # URL-encode the path component to handle spaces etc.
        safe_path = quote(rel_url, safe="/")
        url = urljoin(HARTI_BASE, safe_path)

        try:
            time.sleep(delay)
            resp = session.get(url, headers=_HEADERS, timeout=60)
            resp.raise_for_status()
            if len(resp.content) < 500:
                logger.warning("Suspiciously small PDF (%d bytes): %s", len(resp.content), url)
                n_fail += 1
                continue
            local_path.write_bytes(resp.content)
            n_ok += 1
            cached.append((date_str, local_path))
            logger.info("[%s] Downloaded %.1f KB -> %s", date_str, len(resp.content) / 1024, local_path.name)
        except Exception as exc:
            logger.warning("[%s] Download FAILED: %s -- %s", date_str, url, exc)
            n_fail += 1

    logger.info(
        "Download complete: %d downloaded, %d skipped (already existed), %d failed",
        n_ok, n_skip, n_fail,
    )
    return cached
