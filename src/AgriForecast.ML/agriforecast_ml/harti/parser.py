"""HARTI PDF parser — dual-format, multi-market column extractor.

Auto-detects the English vegetable table page by scanning for "Dambulla"
in the column-3 header (CRITICAL: ~50% of historical PDFs are 2-page
English/Sinhala; hardcoding a page index would drop half the history).

Multi-market extraction (R1.1 P1 / ClickUp 86cahef3e):
  The same table carries columns for several markets (Dambulla, Pettah/
  Peliyagoda, Kandy, Meegoda, ...).  Each target market's column is located
  by matching its header text via ``_locate_market_column`` — NEVER by a
  fixed position.  Column order and count both drift across the 11-year
  corpus (7, 9, 10 columns observed; "Pettah" was renamed to "Peliyagoda"
  in the bulletin header around 2021, with an occasional typo'd
  "Peliyagod").  A market whose header cannot be positively located is
  skipped for that PDF (WARN, not a hard parse failure) so a layout drift
  never silently reads the wrong market's numbers under the wrong
  market_id (risk R1, PRD  2.6).

  Narahenpita and an arrivals/volume column were investigated against the
  full local PDF cache (2,972 files, 2015-2026) and the live harti.gov.lk
  daily-price.php listing: neither appears anywhere in this daily
  wholesale bulletin series.  Narahenpita is a HARTI *retail* market and
  arrivals volumes appear only in HARTI's separate weekly bulletin (a
  different multi-page narrative PDF format), not the daily table parsed
  here.  Both are still wired up as detect-don't-hardcode lookups
  (_MARKET_HEADER_ALIASES / arrivals-column search) so they activate
  automatically the day either appears in a daily bulletin, but today they
  will legitimately WARN-skip / come back NULL on every PDF in this
  series.  See harti_multimarket_audit.md for the evidence.

Returns (min_price, max_price) tuples — NOT midpoint — for each crop found
in a located market column.

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

# --------------------------------------------------------------------------
# Multi-market column headers (R1.1 P1 / ClickUp 86cahef3e)
# --------------------------------------------------------------------------
# Markets extracted from the daily bulletin, keyed by the canonical market
# name the loader resolves against the DB Markets dimension.  Each entry
# lists the exact header substrings observed in the wild for that market, in
# preference order.  This is a DOCUMENTED ALIAS LIST, not fuzzy matching:
# "Pettah" was renamed to "Peliyagoda" in the bulletin header around 2021
# (same physical market, HARTI's own relabelling), with an occasional typo
# "Peliyagod" (missing trailing 'a', seen 2026-02-17).  If a market's header
# text drifts to something NOT in this list, that is a hard skip (WARN) --
# no positional fallback is ever taken (risk R1, PRD  2.6).
_MARKET_HEADER_ALIASES: dict[str, tuple[str, ...]] = {
    "Dambulla":     ("Dambulla",),
    "Pettah":       ("Pettah", "Peliyagoda", "Peliyagod"),
    "Narahenpita":  ("Narahenpita",),
}

# Header text that marks an arrivals/volume column, if the bulletin ever
# carries one.  Detect-don't-hardcode: absent column => NULL arrivals, not
# an error (see _locate_arrivals_column).
_ARRIVALS_HEADER_MARKERS: tuple[str, ...] = ("Arrival", "arrival", "Volume", "volume")


@dataclass
class ParsedPrice:
    date_str: str          # "YYYY-MM-DD"
    harti_label: str       # canonical HARTI label (post-consolidation)
    raw_cell: str          # verbatim cell text (for auditing)
    min_price: float
    max_price: float
    market_name: str = "Dambulla"   # canonical market name (R1.1 P1); default keeps
                                     # pre-multi-market callers/tests (Dambulla-only) working
    arrivals_kg: float | None = None
    pdf_creation_date_raw: str | None = None   # PDF /CreationDate metadata (raw "D:..."
                                                # string); the loader resolves this into
                                                # PriceObservation.AsOfUtc (bulletin vintage)


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


def _locate_market_column(table: list[list], market_name: str) -> int | None:
    """Locate ``market_name``'s column index by scanning the first 3 header rows.

    Detect-don't-hardcode (risk R1, PRD  2.6): matches header cell text against
    ``_MARKET_HEADER_ALIASES[market_name]`` (falls back to ``(market_name,)`` for
    an unregistered name).  Returns the column index if a positive match is
    found, or None if it cannot be located.  Callers MUST treat None as a hard
    skip for that market — never fall back to a hardcoded/positional column;
    over 11 years of varying layouts (7, 9, 10 observed column counts) that
    would silently read the wrong market's numbers under the wrong market_id.
    """
    aliases = _MARKET_HEADER_ALIASES.get(market_name, (market_name,))
    for row in table[:3]:
        if not row:
            continue
        for ci, cell in enumerate(row):
            if not cell:
                continue
            cell_text = _clean(cell)
            if any(alias in cell_text for alias in aliases):
                return ci
    return None  # fail-loud: caller skips this market for this PDF


def _dambulla_col_index(table: list[list]) -> int | None:
    """Back-compat wrapper — Dambulla-only column lookup.

    Kept as a thin alias over ``_locate_market_column`` so existing callers/
    tests that only knew about Dambulla keep working unchanged.
    """
    return _locate_market_column(table, "Dambulla")


def _locate_arrivals_column(table: list[list]) -> int | None:
    """Locate an arrivals/volume column by header text, if the bulletin has one.

    Detect-don't-hardcode, same contract as ``_locate_market_column``: returns
    the column index on a positive header match, else None.  An absent column
    is NOT an error — callers must treat None as "no arrivals data in this
    PDF" and emit NULL arrivals, never guess a column.
    """
    for row in table[:3]:
        if not row:
            continue
        for ci, cell in enumerate(row):
            if not cell:
                continue
            cell_text = _clean(cell)
            if any(marker in cell_text for marker in _ARRIVALS_HEADER_MARKERS):
                return ci
    return None


def _parse_arrivals_cell(cell: object) -> float | None:
    """Parse an arrivals/volume cell into a float kg value, or None.

    Reuses the same empty-cell vocabulary as prices (market-closed / no data).
    Arrivals cells are single numbers (not ranges); a range-shaped cell here
    would be unexpected input, so we conservatively return None rather than
    guessing which side of the range is the volume.
    """
    s = _clean(cell)
    if not s or s in _EMPTY_CELLS:
        return None
    m = _SINGLE_RE.match(s)
    if m:
        return float(m.group(1).replace(",", ""))
    return None


# Markets extracted from every PDF, in emission order.  Dambulla stays first
# so existing per-PDF ordering (and the Dambulla-only splice/loader path) is
# unaffected by the new markets appended after it.
_TARGET_MARKETS: tuple[str, ...] = ("Dambulla", "Pettah", "Narahenpita")


def parse_pdf(pdf_path: Path, date_str: str) -> list[ParsedPrice]:
    """Parse one HARTI PDF, return a list of ParsedPrice for target crops
    across all locatable target markets (Dambulla, Pettah, Narahenpita).

    Market-closed rows (cell == "-") are NOT returned — the caller should
    treat absence as a market-closed / no-data signal, consistent with the
    "keep zero-price rows, filter at feature time" policy.

    Fail-loud contract (risk R1, PRD  2.6): each market's column is located
    independently via ``_locate_market_column``.  A market whose header
    cannot be found is skipped for THIS PDF only (WARN) — the other
    locatable markets still parse normally.  If NO market column can be
    located at all, the whole PDF is skipped (WARN) exactly as before.
    Positional/index fallback is never used.
    """
    results: list[ParsedPrice] = []

    try:
        # Suppress pdfplumber's colour-space warnings (cosmetic, not errors)
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            import pdfplumber
            with pdfplumber.open(str(pdf_path)) as pdf:
                n_pages = len(pdf.pages)
                pg_idx, table = _find_english_veg_page(pdf)
                # PDF /CreationDate -- the bulletin's real publication timestamp
                # (frequently the day AFTER date_str; bulletins are typically
                # finalised the next morning).  Carried through to the loader,
                # which resolves it into PriceObservation.AsOfUtc.
                pdf_creation_date_raw = pdf.metadata.get("CreationDate")

        if table is None:
            logger.warning(
                "[%s] No English veg page found in %s (total pages=%d)",
                date_str, pdf_path.name,
                n_pages,
            )
            return results

        # Locate each target market's column independently; missing ones are
        # dropped from this PDF's run (WARN), never positionally guessed.
        market_cols: dict[str, int] = {}
        for market_name in _TARGET_MARKETS:
            col = _locate_market_column(table, market_name)
            if col is None:
                logger.warning(
                    "[%s] %s column header not located in %s (pg%d) — skipping "
                    "this market for this PDF to avoid reading the wrong column",
                    date_str, market_name, pdf_path.name, pg_idx,
                )
                continue
            market_cols[market_name] = col

        if not market_cols:
            logger.warning(
                "[%s] English veg page found (pg%d) but NO target market column "
                "could be located — skipping PDF entirely",
                date_str, pg_idx,
            )
            return results

        # Arrivals/volume column, if the bulletin carries one (detect-don't-
        # hardcode; absent column => NULL arrivals on every row, not an error).
        arrivals_col = _locate_arrivals_column(table)

        # Walk all rows looking for target crops in column 0
        seen_crops: dict[str, set[str]] = {m: set() for m in market_cols}  # per-market dedup
        for row in table:
            if not row or not row[0]:
                continue
            raw_crop = _clean(row[0])
            canonical = _TARGET_CROPS.get(raw_crop)
            if canonical is None:
                continue

            arrivals_kg = None
            if arrivals_col is not None:
                arrivals_raw = _clean(row[arrivals_col]) if len(row) > arrivals_col else ""
                arrivals_kg = _parse_arrivals_cell(arrivals_raw)

            for market_name, col in market_cols.items():
                if canonical in seen_crops[market_name]:
                    # Bitter Gourd pre-split may appear twice; take first occurrence
                    continue
                seen_crops[market_name].add(canonical)

                raw_cell = _clean(row[col]) if len(row) > col else ""
                parsed = _parse_price_cell(raw_cell)
                if parsed is None:
                    # Market closed / no data — do not emit a row
                    logger.debug(
                        "[%s] %s/%s: market-closed/missing (%r)",
                        date_str, market_name, canonical, raw_cell,
                    )
                    continue

                results.append(ParsedPrice(
                    date_str=date_str,
                    harti_label=canonical,
                    raw_cell=raw_cell,
                    min_price=parsed[0],
                    max_price=parsed[1],
                    market_name=market_name,
                    arrivals_kg=arrivals_kg,
                    pdf_creation_date_raw=pdf_creation_date_raw,
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
    # Per-year tally for corpus-coverage visibility
    from collections import Counter
    year_detected: Counter = Counter()   # PDFs where rows were extracted
    year_skipped: Counter = Counter()    # PDFs that produced zero rows

    for i, (date_str, path) in enumerate(sorted(cached_pdfs, key=lambda x: x[0])):
        rows = parse_pdf(path, date_str)
        yr = date_str[:4]
        if rows:
            n_ok += 1
            year_detected[yr] += 1
        else:
            n_err += 1
            year_skipped[yr] += 1
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
    # Per-year detected-vs-skipped tally (corpus-coverage visibility)
    all_years = sorted(set(year_detected) | set(year_skipped))
    for yr in all_years:
        detected = year_detected[yr]
        skipped = year_skipped[yr]
        total_yr = detected + skipped
        logger.info(
            "Coverage %s: %d/%d PDFs yielded prices, %d skipped (Sinhala-only or bad layout)",
            yr, detected, total_yr, skipped,
        )
    return all_rows
