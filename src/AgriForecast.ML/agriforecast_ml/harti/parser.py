"""HARTI PDF parser — dual-format, Dambulla-column extractor.

Auto-detects the English vegetable table page by scanning for "Dambulla"
in the column-3 header (CRITICAL: ~50% of historical PDFs are 2-page
English/Sinhala; hardcoding a page index would drop half the history).

Returns (min_price, max_price) tuples — NOT midpoint — for each crop found
in the Dambulla column.

Bitter Gourd name consolidation:
  Pre-2023: "Bitter Gourd (Other)" and "Bitter Gourd" may both appear.
  Post-split: they are separate.  We consolidate both to "Bitter Gourd".
"""
from __future__ import annotations

import re
import logging
import warnings
from dataclasses import dataclass
from datetime import date
from pathlib import Path

logger = logging.getLogger(__name__)

# HARTI crop labels we care about.  Keys are the exact strings seen in PDFs;
# value is the canonical HARTI label passed to the loader for CropId mapping.
_TARGET_CROPS: dict[str, str] = {
    "Beans":                "Beans",
    "Ladies Fingers":       "Ladies Fingers",
    "Capsicum":             "Capsicum",
    "Bitter Gourd":         "Bitter Gourd",
    "Bitter Gourd (Other)": "Bitter Gourd",   # pre-split variant → same slot
    "Luffa":                "Luffa",
    "Snake Gourd":          "Snake Gourd",
}

# Cells that mean "no data / market closed"
_EMPTY_CELLS = {"-", "", "N/A", "n/a", "NA", "nil", "Nil", "-\n"}

# Price cell pattern: optional spaces around the hyphen separator
#   "130.00- 160.00"  "600- 800"  "600 - 800"
_RANGE_RE = re.compile(
    r"([\d,]+(?:\.\d+)?)\s*-\s*([\d,]+(?:\.\d+)?)"
)
# Single numeric value (market gave one price, treated as min=max)
_SINGLE_RE = re.compile(r"^([\d,]+(?:\.\d+)?)$")


@dataclass
class ParsedPrice:
    date_str: str          # "YYYY-MM-DD"
    harti_label: str       # canonical HARTI label (post-consolidation)
    raw_cell: str          # verbatim cell text (for auditing)
    min_price: float
    max_price: float


def _clean(cell: object) -> str:
    """Normalise a table cell to a stripped string."""
    if cell is None:
        return ""
    return str(cell).strip()


def _parse_price_cell(cell: object) -> tuple[float, float] | None:
    """Parse a HARTI price cell into (min, max).

    Returns None if the cell is empty / market-closed.
    """
    s = _clean(cell)
    if not s or s in _EMPTY_CELLS:
        return None

    m = _RANGE_RE.search(s)
    if m:
        lo = float(m.group(1).replace(",", ""))
        hi = float(m.group(2).replace(",", ""))
        # Guard: occasionally lo > hi due to typo; swap silently
        if lo > hi:
            lo, hi = hi, lo
        return (lo, hi)

    m2 = _SINGLE_RE.match(s)
    if m2:
        v = float(m2.group(1).replace(",", ""))
        return (v, v)

    return None


def _find_english_veg_page(pdf) -> tuple[int | None, list[list] | None]:
    """Scan pages for the English vegetable table.

    Detection: look for a header row whose column-3 cell contains "Dambulla".
    This is robust across both the old 3-page format and the newer 11-page
    format.  We scan up to the first 3 rows of each page's main table.

    Returns (page_index, table) or (None, None).
    """
    for pg_idx, page in enumerate(pdf.pages):
        tables = page.extract_tables()
        if not tables:
            continue
        t = tables[0]
        if not t:
            continue
        # Scan first 3 rows for a "Dambulla" marker in column 3
        for row in t[:3]:
            if not row or len(row) <= 3:
                continue
            cell3 = _clean(row[3])
            if "Dambulla" in cell3 or "dambulla" in cell3.lower():
                return pg_idx, t

    return None, None


def _dambulla_col_index(table: list[list]) -> int:
    """Confirm Dambulla column index from the header rows (usually 3).

    Returns the column index, defaulting to 3 if not found (belt-and-suspenders).
    """
    for row in table[:3]:
        if not row:
            continue
        for ci, cell in enumerate(row):
            if cell and "Dambulla" in _clean(cell):
                return ci
    return 3


def parse_pdf(pdf_path: Path, date_str: str) -> list[ParsedPrice]:
    """Parse one HARTI PDF, return a list of ParsedPrice for target crops.

    Market-closed rows (cell == "-") are NOT returned — the caller should
    treat absence as a market-closed / no-data signal, consistent with the
    "keep zero-price rows, filter at feature time" policy.
    """
    results: list[ParsedPrice] = []

    try:
        # Suppress pdfplumber's colour-space warnings (cosmetic, not errors)
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            import pdfplumber
            with pdfplumber.open(str(pdf_path)) as pdf:
                pg_idx, table = _find_english_veg_page(pdf)

        if table is None:
            logger.warning(
                "[%s] No English veg page found in %s (total pages=%d)",
                date_str, pdf_path.name,
                sum(1 for _ in range(100)),  # lazy; we already know from open
            )
            return results

        dambulla_col = _dambulla_col_index(table)

        # Walk all rows looking for target crops in column 0
        seen_crops: set[str] = set()  # deduplicate within one PDF
        for row in table:
            if not row or not row[0]:
                continue
            raw_crop = _clean(row[0])
            canonical = _TARGET_CROPS.get(raw_crop)
            if canonical is None:
                continue
            if canonical in seen_crops:
                # Bitter Gourd pre-split may appear twice; take first occurrence
                continue
            seen_crops.add(canonical)

            raw_cell = _clean(row[dambulla_col]) if len(row) > dambulla_col else ""
            parsed = _parse_price_cell(raw_cell)
            if parsed is None:
                # Market closed / no data — do not emit a row
                logger.debug("[%s] %s: market-closed/missing (%r)", date_str, canonical, raw_cell)
                continue

            results.append(ParsedPrice(
                date_str=date_str,
                harti_label=canonical,
                raw_cell=raw_cell,
                min_price=parsed[0],
                max_price=parsed[1],
            ))

    except Exception as exc:
        logger.error("[%s] Parse error on %s: %s", date_str, pdf_path.name, exc, exc_info=True)

    return results


def parse_many(
    cached_pdfs: list[tuple[str, Path]],
    *,
    log_every: int = 100,
) -> list[ParsedPrice]:
    """Parse a batch of (date_str, path) tuples.

    Args:
        cached_pdfs:  Output of downloader.download_pdfs().
        log_every:    Log progress every N PDFs.

    Returns:
        Flat list of ParsedPrice across all successfully parsed PDFs.
    """
    all_rows: list[ParsedPrice] = []
    n_total = len(cached_pdfs)
    n_ok = n_err = 0

    for i, (date_str, path) in enumerate(sorted(cached_pdfs, key=lambda x: x[0])):
        rows = parse_pdf(path, date_str)
        if rows:
            n_ok += 1
        else:
            n_err += 1
        all_rows.extend(rows)

        if (i + 1) % log_every == 0 or (i + 1) == n_total:
            logger.info(
                "Parsed %d/%d PDFs — %d with prices, %d empty/error, %d rows so far",
                i + 1, n_total, n_ok, n_err, len(all_rows),
            )

    logger.info(
        "parse_many complete: %d rows from %d PDFs (%d empty/error)",
        len(all_rows), n_ok, n_err,
    )
    return all_rows
