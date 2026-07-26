"""HARTI PDF parser: dual-format, multi-market column extractor.

Finds the English vegetable table page by scanning for 'Dambulla' in the column-3 header.
About half the historical PDFs are 2-page English/Sinhala, so a hardcoded page index would
drop half the history.

Each target market's column is located by matching its header text, NEVER by position:
column order and count drift across the 11-year corpus, and headers get renamed and
mis-spelled. A market whose header cannot be located is skipped for that PDF with a WARN,
so a layout change can never silently read one market's numbers under another market's id.

Returns (min_price, max_price) tuples, not midpoints, for every target crop found in a
located market column.

Bitter Gourd and Brinjals appeared as split '(Other)'/'(Village)' rows before 2023-02 and
as a single consolidated row afterwards; the pre-split variants map to the same canonical
label.

Fruits flow ONLY through the multi-market PriceObservations path. They are deliberately
absent from the loader's legacy Dambulla-only MarketPrices maps, so no fruit row can ever
reach that path.
"""
from __future__ import annotations

import os
import re
import logging
import warnings
from concurrent.futures import ThreadPoolExecutor, TimeoutError as FutureTimeoutError
from dataclasses import dataclass
from datetime import date
from pathlib import Path

logger = logging.getLogger(__name__)

# Parser DoS guard: a wall-clock timeout per PDF. A pathological or malicious PDF can be
# small on disk yet extremely expensive to parse, and would otherwise stall the whole
# ingestion pass. One that blows the budget is treated like any other unparseable PDF
# (WARN, zero rows, move on), never a hard failure. Configurable via
# AGRI_HARTI_PARSE_TIMEOUT_SECONDS; 0 or negative disables it. The default is far more
# than a real bulletin needs, so a full-corpus backfill is unaffected.
_DEFAULT_PARSE_TIMEOUT_SECONDS = 120.0


def _parse_timeout_seconds() -> float:
    raw = os.getenv("AGRI_HARTI_PARSE_TIMEOUT_SECONDS")
    if raw is None:
        return _DEFAULT_PARSE_TIMEOUT_SECONDS
    try:
        return float(raw)
    except ValueError:
        return _DEFAULT_PARSE_TIMEOUT_SECONDS

# HARTI crop labels we care about. Keys are the exact strings seen in the PDFs; the value
# is the canonical label passed to the loader for CropId mapping. Matching is EXACT dict
# lookup, never substring, so 'Beans' and 'Long Beans' cannot collide.
#
# Bitter Gourd and Brinjals appear as '(Village)'/'(Other)' rows until 2023-01 and as one
# consolidated row from 2023-02; both eras map to the same canonical label.
#
# The 'Eggplant' row that appears from 2023-02 is HARTI relabelling the Wing Beans row,
# not an aubergine variety, so it is deliberately NOT mapped: doing so would merge two
# genuinely different commodities.
_TARGET_CROPS: dict[str, str] = {
    "Beans":                "Beans",
    "Ladies Fingers":       "Ladies Fingers",
    "Capsicum":             "Capsicum",
    "Bitter Gourd":         "Bitter Gourd",
    "Bitter Gourd (Other)": "Bitter Gourd",   # pre-2023-02 split variant → same slot
    "Luffa":                "Luffa",
    "Snake Gourd":          "Snake Gourd",

    # 'Pumpkin' is deliberately excluded: the bulletin has one generic row while the DB only
    # has 'Pumpkin - Big' and 'Pumpkin - Malashian', so mapping it would be guessing a variety.
    "Green Chillies":  "Green Chillies",
    "Tomato":          "Tomato",
    "Leeks":           "Leeks",
    "Knolkhol":        "Knolkhol",
    "Raddish":         "Raddish",
    "Cucumber":        "Cucumber",
    "Drumstick":       "Drumstick",
    "Long Beans":      "Long Beans",
    "Ash Plantains":   "Ash Plantains",
    "Lime":            "Lime",
    "Sweet Potatoe":   "Sweet Potato",   # HARTI's own spelling (typo, corpus-wide)
    "Manioc":          "Manioc",

    # Brinjals: pre/post 2023-02 consolidation, the same pattern as Bitter Gourd above.
    "Brinjals":          "Brinjals",
    "Brinjals (Village)": "Brinjals",
    "Brinjals (Other)":   "Brinjals",

    # Potatoes: the bulletin names three varieties, each its own DB crop, so they are not
    # consolidated. Both the spaced and unspaced 'Potato(Imported)' spellings are kept.
    "Potato(Imported)":     "Potato (Imported)",
    "Potato (Imported)":    "Potato (Imported)",
    "Potato (Welimada)":    "Potato (Welimada)",
    "Potato (Nuwaraeliya)": "Potato (Nuwaraeliya)",

    # Onions: the bulletin only ever carries these two rows, so a plain 'Big Onion' or any
    # 'Red Onion' DB crop stays unmapped.
    "B'Onion Imported": "Big Onion Imported",
    "Big-onion Local":  "Big Onion Local",
}

# Fruits: per-kg rows only; per-fruit-unit rows are deliberately skipped.
#
# The same table carries a fruit subsection below the vegetables. Unlike the vegetable
# rows, which are Rs/kg corpus-wide, fruit rows do not share one unit: each row's own label
# carries it, either the page-level '(Rs./kg)' default (no suffix) or an explicit
# '(Rs/Fruit)' override baked into the row text. A per-fruit price must NEVER be stored as
# a per-kg observation, which is why the two dicts below are kept separate.
#
# Only the four fruits that resolve to an existing per-kg DB crop are targeted. Every other
# fruit row is per-fruit corpus-wide with no per-kg crop to map to, so it falls through the
# ordinary 'not a target row' branch like any other non-target row.
#
# HARTI's labels are not stable: Kolikuttu flips between '(Rs/Fruits)' and a bare label
# several times, including two reversions. The split-dict design handles that with no extra
# logic, because each PDF's own row label decides independently - no date or era heuristic
# is ever consulted.
# The canonical values below are byte-for-byte what the CommodityAliases seed migration
# writes for Source='HARTI'.
_TARGET_FRUITS_PER_KG: dict[str, str] = {
    "Ambul":          "Ambul",
    "Ambul(Rs/Kg)":   "Ambul",
    "Kolikuttu":      "Kolikuttu",
    "Seeni":          "Seeni",
    "Papaya":         "Papaya",
    "Papaya (Rs/Kg)": "Papaya",
}

# Per-fruit-unit label variants of the SAME four fruits, matched positively so a skip can
# be logged with the canonical name attached - 'known per-fruit row, deliberately skipped'
# rather than 'not a target row'. Never add these to _TARGET_FRUITS_PER_KG: that is exactly
# the unit-mismatch bug this split prevents.
_TARGET_FRUITS_PER_FRUIT_SKIP: dict[str, str] = {
    "Kolikuttu (Rs/Fruits)": "Kolikuttu",
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

# Multi-market column headers.
# Markets extracted from the daily bulletin, keyed by the canonical name the loader
# resolves against the DB Markets dimension. Each entry lists the exact header substrings
# observed in the wild, in preference order. This is a DOCUMENTED ALIAS LIST, not fuzzy
# matching: header text drifts (Pettah was renamed Peliyagoda around 2021) and pdfplumber
# cell-bleed artifacts recur, where a neighbouring cell's letters bleed across and produce
# spellings like 'aKeppetipola' or 'NorochcholeT'. A header that drifts to something not
# listed here is a hard skip with a WARN - no positional fallback, ever.
#
# Markets enter the bulletin at different points (Keppetipola and Nuwara Eliya from
# 2017-03, Bandarawela from 2021-12, Veyangoda from 2022-02), so an absent column is normal
# for older PDFs. The aliases are substring-safe: no alias of one market is a substring of
# another market's header text.
_MARKET_HEADER_ALIASES: dict[str, tuple[str, ...]] = {
    "Dambulla":     ("Dambulla",),
    "Pettah":       ("Pettah", "Peliyagoda", "Peliyagod", "Petha"),
    "Narahenpita":  ("Narahenpita",),
    "Thambuttegama": (
        "Thambuththegama", "T'thegama", "Thambuththegam", "hambuththegam",
        "ambuththega",
    ),
    "Keppetipola": (
        "Kappetipola", "Keppetipola", "aKappetipola", "aKeppetipola",
        "maKappetipola",
    ),
    "Kandy":        ("Kandy",),
    "Meegoda":      ("Meegoda",),
    "Norochchole":  ("Norochchole", "NorochcholeT", "NorochcholTeh"),
    "Nuwara Eliya": ("Nuwaraeliya", "Nuwara Eliya"),
    "Bandarawela":  ("Bandarawela",),
    "Veyangoda":    ("Veyangoda", "Veyangod"),
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
    market_name: str = "Dambulla"  # canonical market name; the default keeps
                                   # Dambulla-only callers and tests working
    arrivals_kg: float | None = None
    pdf_creation_date_raw: str | None = None  # raw PDF /CreationDate string; the loader
                                              # resolves it into PriceObservation.AsOfUtc


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


# Markets extracted from every PDF, in emission order. Dambulla stays first so the
# Dambulla-only splice/loader path and the existing per-PDF ordering are unaffected; later
# markets are appended for the same back-compat reason.
_TARGET_MARKETS: tuple[str, ...] = (
    "Dambulla", "Pettah", "Narahenpita", "Thambuttegama", "Keppetipola",
    "Kandy", "Meegoda", "Norochchole", "Nuwara Eliya", "Bandarawela",
    "Veyangoda",
)


def parse_pdf(pdf_path: Path, date_str: str) -> list[ParsedPrice]:
    """Parse one HARTI PDF, return a list of ParsedPrice for target crops
    across all locatable target markets (Dambulla, Pettah, Narahenpita,
    Thambuttegama, Keppetipola).

    Market-closed rows (cell == "-") are NOT returned — the caller should
    treat absence as a market-closed / no-data signal, consistent with the
    "keep zero-price rows, filter at feature time" policy.

    Fail-loud contract (risk R1, PRD  2.6): each market's column is located
    independently via ``_locate_market_column``.  A market whose header
    cannot be found is skipped for THIS PDF only (WARN) — the other
    locatable markets still parse normally.  If NO market column can be
    located at all, the whole PDF is skipped (WARN) exactly as before.
    Positional/index fallback is never used.

    Parser DoS guard (S2): the parse body runs under a wall-clock timeout
    (AGRI_HARTI_PARSE_TIMEOUT_SECONDS, default 120s). A PDF that exceeds the
    budget is treated like any other unparseable PDF (WARN, empty result), so a
    pathological/malicious document cannot stall the whole ingestion pass.
    """
    timeout = _parse_timeout_seconds()
    if timeout <= 0:
        return _parse_pdf_impl(pdf_path, date_str)

    # Run the parse in a worker thread and bound it with a wall-clock timeout. On timeout we
    # return empty (exactly like a parse error) and shut the executor down WITHOUT waiting:
    # 'with ThreadPoolExecutor(...)' would block on exit until the slow parse finished, which
    # would defeat the timeout. The abandoned worker unwinds on its own.
    pool = ThreadPoolExecutor(max_workers=1)
    future = pool.submit(_parse_pdf_impl, pdf_path, date_str)
    try:
        result = future.result(timeout=timeout)
        pool.shutdown(wait=False)
        return result
    except FutureTimeoutError:
        logger.warning(
            "[%s] Parse TIMEOUT after %.1fs on %s — skipping this PDF "
            "(possible parser-DoS document); ingestion continues.",
            date_str, timeout, pdf_path.name,
        )
        pool.shutdown(wait=False)
        return []


def _parse_pdf_impl(pdf_path: Path, date_str: str) -> list[ParsedPrice]:
    """Actual parse body for one PDF (wrapped by ``parse_pdf`` for the timeout)."""
    results: list[ParsedPrice] = []

    try:
        # Suppress pdfplumber's colour-space warnings (cosmetic, not errors)
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            import pdfplumber
            with pdfplumber.open(str(pdf_path)) as pdf:
                n_pages = len(pdf.pages)
                pg_idx, table = _find_english_veg_page(pdf)
                # The PDF /CreationDate is the bulletin's real publication timestamp, often the day
                # after date_str. Carried through to the loader, which resolves it into AsOfUtc.
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
                canonical = _TARGET_FRUITS_PER_KG.get(raw_crop)
            if canonical is None:
                skip_fruit = _TARGET_FRUITS_PER_FRUIT_SKIP.get(raw_crop)
                if skip_fruit is not None:
                    logger.debug(
                        "[%s] %s: per-FRUIT unit row (label %r) — skipped, "
                        "never stored as a per-kg observation (unit mismatch)",
                        date_str, skip_fruit, raw_crop,
                    )
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
