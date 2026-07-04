"""CBSL macro PDF parser — labeled-line regex extractor, NOT table extraction.

Step-0 probe finding (2026-07-04, 3 real CCPI releases + MEI_202605 pack,
extracted with this project's pdfplumber): pdfplumber's ``extract_tables()``
detects ~18 fragmented layout "tables" per CCPI press release, and MEI
interest-rate/trade numbers interleave with chart-caption text on the same
page. Table extraction is therefore the WRONG tool here (fine for HARTI's
strict grid bulletins, wrong for these prose-plus-inset-table press
releases). This parser instead runs regexes over ``page.extract_text()``
against known, stable label phrases:

  CCPI press release (2 pages, ~4.8k chars):
    - "Index Value <prev> <curr>"           -> CCPI_BASE2021 level (curr)
    - "Monthly Change (%) <prev> <curr>"     -> not persisted as its own
                                                series (spec keeps only the
                                                4 series below); parsed only
                                                for the drop-counter sanity
                                                check that both numbers exist.
    - "Y-o-Y Inflation (%) <prev> <curr>"    -> CCPI_HEADLINE_YOY_BASE2021
    - "Food inflation (Y-o-Y) ... to/at X%" (verbatim prose, verb varies:
       accelerated/decelerated/moderated/remained unchanged -- the numeric
       capture is anchored ONLY on "Food inflation (Y-o-Y) ... to/at X%",
       decoupled from any trailing month/year) -> CCPI_FOOD_YOY_BASE2021.
       ReferenceDate for this series (like index/headline) always comes
       from the shared column-header/filename resolution below, NEVER from
       text following the food sentence -- pdfplumber's linear extraction
       interleaves inset-chart caption text (which can itself contain a
       stray "2021" from the "CCPI (2021=100)" base-year caption) between
       the food sentence's month and its year on a real release (reviewer
       finding 2026-07-04, F1) -- capturing a trailing "<Month> <Year>" here
       risked reading that stray "2021" as the reference year.
    - Base year: "CCPI, 2021=100" (or "(CCPI 2021=100)" variants) confirms
       the base-year token embedded in every SeriesCode this parser emits.
    - Reference month: the LATTER of the two column headers in the
       "Inflation <PrevMonth> <PrevYear> <CurrMonth> <CurrYear>" line (the
       release's OWN reference period), cross-checked against the filename.

  MEI pack (25 pages; page found by header search, NOT a fixed index):
    - "Food and Beverages" Rs.Mn monthly line -> FOOD_IMPORTS_YOY. The pack's
      EXTERNAL TRADE table carries the CURRENT and PRIOR YEAR figures for a
      single calendar month labelled by the table's OWN column header (e.g.
      "April"), which is ONE MONTH BEHIND the pack's own YYYYMM (a May pack
      carries April trade data) — the reference month is read from the table
      header text, never assumed to be the pack month.
    - "Overnight Policy Rate (OPR)" line in the "21. INTEREST RATES" section
      -> policy_rate (POLICY_RATE_OPR). Adjudicated IN (not dropped): the
      probe shows this specific line is a clean two-column labeled row
      ("Overnight Policy Rate (OPR)  Per cent  <prevMonth> <currMonth>"),
      located by searching for the literal label text (not a fixed page
      index, since the TOC on page 1 also contains the string
      "INTEREST RATES" and must not be mistaken for the data page).

Throw-don't-guess: any of the above anchors not found in a given PDF is a
WARN + skip for THAT SERIES in THAT ARTIFACT (drop-counter), never a
fabricated/interpolated value and never a hard failure that aborts the whole
batch — mirrors harti/parser.py's per-market skip contract.
"""
from __future__ import annotations

import logging
import re
import warnings
from concurrent.futures import ThreadPoolExecutor, TimeoutError as FutureTimeoutError
from dataclasses import dataclass, field
from datetime import date
from pathlib import Path

logger = logging.getLogger(__name__)

# --------------------------------------------------------------------------
# Parser DoS guard (mirrors harti/parser.py's wall-clock timeout).
# --------------------------------------------------------------------------
import os

_DEFAULT_PARSE_TIMEOUT_SECONDS = 120.0


def _parse_timeout_seconds() -> float:
    raw = os.getenv("AGRI_CBSL_PARSE_TIMEOUT_SECONDS")
    if raw is None:
        return _DEFAULT_PARSE_TIMEOUT_SECONDS
    try:
        return float(raw)
    except ValueError:
        return _DEFAULT_PARSE_TIMEOUT_SECONDS


# Post-open page-count cap (spec item: reject beyond ~50 pages). CCPI releases
# are 2 pages; MEI packs are ~25 pages -- 50 gives comfortable headroom while
# still bounding a pathological/oversized PDF that slipped past the 25MB
# download cap (small-on-disk, expensive/huge-to-parse).
_MAX_PAGES = 50

SERIES_CCPI_INDEX = "CCPI_BASE2021"
SERIES_CCPI_FOOD_YOY = "CCPI_FOOD_YOY_BASE2021"
SERIES_CCPI_HEADLINE_YOY = "CCPI_HEADLINE_YOY_BASE2021"
SERIES_FOOD_IMPORTS_YOY = "FOOD_IMPORTS_YOY"
SERIES_POLICY_RATE = "POLICY_RATE_OPR"

_MONTH_NAMES = (
    "January", "February", "March", "April", "May", "June",
    "July", "August", "September", "October", "November", "December",
)
_MONTH_TO_NUM = {m: i + 1 for i, m in enumerate(_MONTH_NAMES)}
_MONTH_ALT = "|".join(_MONTH_NAMES)


@dataclass
class ParsedMacroPoint:
    series_code: str
    reference_date: date        # 1st-of-month, the period the reading describes
    value: float
    source: str                 # "CBSL_CCPI" or "CBSL_MEI"
    pdf_creation_date_raw: str | None = None   # PDF /CreationDate metadata (raw)
    filename_pub_date: date | None = None      # date embedded in the URL/filename
    extra: dict = field(default_factory=dict)  # e.g. {"base_year": "2021"}


# ---------------------------------------------------------------------------
# CCPI press release
# ---------------------------------------------------------------------------

# "Index Value 203.4 207.7" -- two numbers, prev then curr.
_CCPI_INDEX_RE = re.compile(r"Index Value\s+([\d.]+)\s+([\d.]+)")

# "Y-o-Y Inflation (%) 5.5 6.8"
_CCPI_HEADLINE_YOY_RE = re.compile(r"Y-o-Y Inflation \(%\)\s+([\-\d.]+)\s+([\-\d.]+)")

# "Monthly Change (%) 0.9 2.1" -- parsed only for the sanity/drop-counter check.
_CCPI_MONTHLY_CHANGE_RE = re.compile(r"Monthly Change \(%\)\s+([\-\d.]+)\s+([\-\d.]+)")

# Verbatim prose: "Food inflation (Y-o-Y) accelerated to 2.8% in April 2026"
# / "... moderated further to 1.5% in July 2025 ..." / "... remained
# unchanged at 3.0% in December 2025 ..." (verb varies: accelerated /
# decelerated / moderated / remained unchanged -- and the preposition before
# the number varies too: "to" for a changing rate, "at" for "remained
# unchanged at X%" -- both are covered below).
#
# DELIBERATELY DECOUPLED from month/year (reviewer F1, 2026-07-04): an
# earlier version of this regex captured a trailing "<Month> <Year>" right
# after the percentage, using it to derive ReferenceDate. That silently
# failed recall on releases where pdfplumber's linear text extraction
# interleaves an inset chart's caption between the month and the year, e.g.
# (cbsl_ccpi_20250731.pdf, verbatim):
#   "Food inflation (Y-o-Y) moderated further to 1.5% in July\n
#    % CCPI (2021=100)\n2025 from 4.3% recorded in June 2025, ..."
# The month ("July") and year ("2025") are on DIFFERENT lines, separated by
# the chart caption "% CCPI (2021=100)" -- which itself contains the digits
# "2021". A naive widened month/year capture window here reproduced a real
# failure: it read "2021" (the BASE year from the interleaved caption) as
# the reference YEAR, writing a wrong ReferenceDate. The value capture is
# therefore anchored ONLY on the label + verb + percentage; ReferenceDate
# for every series in this release (index, headline, food) is resolved
# uniformly from the release's own column-header line
# (_CCPI_COLUMN_MONTHS_RE, cross-checked against the filename date) --
# see _parse_ccpi_pdf_impl, which no longer branches on this match at all.
#
# Non-Food trap (still present, unchanged): "Non-Food inflation (Y-o-Y)" is
# a superstring match for a naive "Food inflation (Y-o-Y)" pattern -- MUST
# exclude it with a negative lookbehind, or a Non-Food figure silently gets
# written under the Food SeriesCode (wrong number, no error raised --
# exactly the guessing-parser anti-pattern this build must avoid).
_CCPI_FOOD_YOY_RE = re.compile(
    r"(?<!Non-)(?<!Non )Food inflation \(Y-o-Y\)[^.]{0,60}?(?:to|at)\s+([\-\d.]+)%",
    re.IGNORECASE,
)

# Base year token, e.g. "(CCPI, 2021=100)" / "CCPI (2021=100)".
_CCPI_BASE_YEAR_RE = re.compile(r"CCPI,?\s*\(?(\d{4})\s*=\s*100\)?", re.IGNORECASE)

# Reference-month column header, e.g. "Inflation May June" / "CCPI (2021=100) 2026 2026".
# We read the "Inflation <prevMonth> <currMonth>" line directly.
_CCPI_COLUMN_MONTHS_RE = re.compile(
    r"Inflation\s+(" + _MONTH_ALT + r")\s+(" + _MONTH_ALT + r")"
)


def parse_ccpi_pdf(
    pdf_path: Path,
    *,
    filename_pub_date: date | None = None,
) -> list[ParsedMacroPoint]:
    """Parse one CCPI press-release PDF into up to 3 ParsedMacroPoint rows
    (index level, headline YoY, food YoY). Wrapped by the wall-clock timeout
    (see ``parse_ccpi_pdf`` public wrapper below via ``_run_with_timeout``).
    """
    return _run_with_timeout(_parse_ccpi_pdf_impl, pdf_path, filename_pub_date=filename_pub_date)


def _parse_ccpi_pdf_impl(
    pdf_path: Path,
    *,
    filename_pub_date: date | None = None,
) -> list[ParsedMacroPoint]:
    results: list[ParsedMacroPoint] = []
    label = pdf_path.name

    try:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            import pdfplumber
            with pdfplumber.open(str(pdf_path)) as pdf:
                n_pages = len(pdf.pages)
                if n_pages > _MAX_PAGES:
                    logger.warning(
                        "[%s] Page count %d exceeds cap %d -- rejecting PDF",
                        label, n_pages, _MAX_PAGES,
                    )
                    return results
                pdf_creation_date_raw = pdf.metadata.get("CreationDate")
                full_text = "\n".join((pg.extract_text() or "") for pg in pdf.pages)
    except Exception as exc:
        logger.error("[%s] CCPI parse error: %s", label, exc, exc_info=True)
        return results

    # --- reference month (from the release's own column-header line) -----
    # Single source of truth for EVERY series in this release (index,
    # headline YoY, food YoY): the "Inflation <prevMonth> <currMonth>"
    # column-header line, with the year inferred from filename_pub_date.
    # Deliberately NEVER derived from the food-inflation sentence itself
    # (reviewer F1, 2026-07-04) -- see _CCPI_FOOD_YOY_RE's docstring for why
    # that coupling was unsafe (a stray "2021" base-year caption could be
    # read as the reference year on some releases).
    ref_date = None
    m_cols = _CCPI_COLUMN_MONTHS_RE.search(full_text)
    m_food = _CCPI_FOOD_YOY_RE.search(full_text)
    if m_cols and filename_pub_date is not None:
        # Current column = the release's own reference month; the year is
        # inferred from filename_pub_date (release always publishes within
        # the reference month or the following days).
        curr_month_name = m_cols.group(2)
        curr_month_num = _MONTH_TO_NUM[curr_month_name.title()]
        year = filename_pub_date.year
        # If the release month rolls back past December -> January boundary
        # relative to the filename's month, adjust the year down by one.
        if curr_month_num > filename_pub_date.month:
            year -= 1
        ref_date = date(year, curr_month_num, 1)

    if ref_date is None:
        logger.warning(
            "[%s] Could not determine reference month for CCPI release -- "
            "skipping ALL series for this artifact (throw-don't-guess)",
            label,
        )
        return results

    base_year_match = _CCPI_BASE_YEAR_RE.search(full_text)
    base_year = base_year_match.group(1) if base_year_match else None
    if base_year is None:
        logger.warning(
            "[%s] No base-year token ('CCPI, YYYY=100') found in text -- "
            "skipping ALL series for this artifact (base year is part of "
            "every SeriesCode; never guessed)",
            label,
        )
        return results
    if base_year != "2021":
        logger.warning(
            "[%s] Unexpected CCPI base year %r (expected 2021) -- this is a "
            "rebase event; SeriesCode tokens in this parser are hardcoded to "
            "BASE2021, so this artifact is SKIPPED rather than silently "
            "mis-keyed. A rebase requires a new SeriesCode token + explicit "
            "code change, never automatic.",
            label, base_year,
        )
        return results

    common = dict(
        reference_date=ref_date,
        source="CBSL_CCPI",
        pdf_creation_date_raw=pdf_creation_date_raw,
        filename_pub_date=filename_pub_date,
        extra={"base_year": base_year},
    )

    # --- index level --------------------------------------------------
    m_idx = _CCPI_INDEX_RE.search(full_text)
    if m_idx:
        try:
            value = float(m_idx.group(2))
            results.append(ParsedMacroPoint(
                series_code=SERIES_CCPI_INDEX, value=value, **common,
            ))
        except ValueError:
            logger.warning("[%s] Index Value found but not numeric: %r", label, m_idx.group(2))
    else:
        logger.warning("[%s] 'Index Value' line not found -- CCPI_BASE2021 skipped", label)

    # --- headline YoY ---------------------------------------------------
    m_yoy = _CCPI_HEADLINE_YOY_RE.search(full_text)
    if m_yoy:
        try:
            value = float(m_yoy.group(2))
            results.append(ParsedMacroPoint(
                series_code=SERIES_CCPI_HEADLINE_YOY, value=value, **common,
            ))
        except ValueError:
            logger.warning("[%s] Y-o-Y Inflation found but not numeric: %r", label, m_yoy.group(2))
    else:
        logger.warning("[%s] 'Y-o-Y Inflation (%%)' line not found -- CCPI_HEADLINE_YOY_BASE2021 skipped", label)

    # --- food YoY (verbatim prose) ---------------------------------------
    if m_food:
        try:
            value = float(m_food.group(1))
            results.append(ParsedMacroPoint(
                series_code=SERIES_CCPI_FOOD_YOY, value=value, **common,
            ))
        except ValueError:
            logger.warning("[%s] Food inflation YoY found but not numeric: %r", label, m_food.group(1))
    else:
        logger.warning(
            "[%s] 'Food inflation (Y-o-Y) ... to/at X%%' prose not found -- "
            "CCPI_FOOD_YOY_BASE2021 skipped",
            label,
        )

    # Sanity cross-check (log-only, never gates): Monthly Change should exist too.
    if not _CCPI_MONTHLY_CHANGE_RE.search(full_text):
        logger.debug("[%s] 'Monthly Change (%%)' line not found (sanity check only)", label)

    return results


# ---------------------------------------------------------------------------
# MEI pack: food-imports YoY + policy rate
# ---------------------------------------------------------------------------

# EXTERNAL TRADE section: the "Food and Beverages" Rs.Mn block. The monthly
# line is the FIRST numeric line under the "Food and Beverages" header (the
# "January - April" cumulative line follows immediately after and must NOT be
# confused with it) -- anchored on the immediately-preceding month-name token
# from the table's OWN column, not the pack's own month.
#
# Structure observed (verbatim, see cbsl_macro parser probe):
#   Food and Beverages
#   April 47,682 74,600 56.5
#   January - April 235,652 232,993 (1.1)
_MEI_FOOD_IMPORTS_BLOCK_RE = re.compile(
    r"Food and Beverages\s*\n\s*(" + _MONTH_ALT + r")\s+"
    r"([\d,]+(?:\.\d+)?)\s+([\d,]+(?:\.\d+)?)\s+"
    r"\(?(-?[\d.]+)\)?"
)

# "21. INTEREST RATES" section header (used only to bound the search window;
# the OPR line itself is located directly by its own label so a TOC mention
# of the same phrase on page 1 is never mistaken for the data page).
_MEI_INTEREST_RATES_HEADER_RE = re.compile(r"^\s*21\.\s*INTEREST RATES", re.MULTILINE)

# "Overnight Policy Rate (OPR) Per cent 7.75 8.75 1 00" -- capture the two
# rate values (prev, curr); the trailing "1 00"/"100" basis-point change is
# NOT captured (redundant with curr-prev, and its own spacing is unstable).
_MEI_OPR_RE = re.compile(
    r"Overnight Policy Rate \(OPR\)\s+Per cent\s+([\d.]+)\s+([\d.]+)"
)

# MEI trade table's own reference-month column header, e.g. "April 2025 April 2026(a)"
# or "Item Unit 2025 2026(a)" + the monthly row's leading month name (captured
# directly by _MEI_FOOD_IMPORTS_BLOCK_RE group 1 already) -- year is inferred
# from the pack's own YYYYMM (the trade table's current-year column always
# matches the pack's year, since the 1-month lag never crosses a year AND a
# month boundary simultaneously in the observed corpus; see parser tests for
# the boundary case of a January pack referencing December of the prior year).


def parse_mei_pdf(
    pdf_path: Path,
    *,
    pack_yyyymm: str,
) -> list[ParsedMacroPoint]:
    """Parse one MEI pack PDF into up to 2 ParsedMacroPoint rows (food-imports
    YoY, policy rate). Wrapped by the wall-clock timeout.
    """
    return _run_with_timeout(_parse_mei_pdf_impl, pdf_path, pack_yyyymm=pack_yyyymm)


def _mei_reference_month(pack_yyyymm: str, month_name: str) -> date:
    """Resolve a trade-table row's reference month/year.

    The MEI pack's OWN month is 1 month AHEAD of the trade table's data (a May
    pack = pack_yyyymm '202605' carries April data). We derive the reference
    year by: start from the pack's (year, month), step back one calendar
    month, and if the row's month name does not match that stepped-back month
    exactly, fall back to matching the row's month within +/- 1 month of the
    stepped-back month (never silently wrong across a year boundary, e.g. a
    January pack -> December of the PRIOR year).
    """
    pack_year = int(pack_yyyymm[:4])
    pack_month = int(pack_yyyymm[4:6])
    # Step back one month from the pack month.
    if pack_month == 1:
        expected_year, expected_month = pack_year - 1, 12
    else:
        expected_year, expected_month = pack_year, pack_month - 1

    row_month_num = _MONTH_TO_NUM[month_name.title()]
    if row_month_num == expected_month:
        return date(expected_year, expected_month, 1)

    # Defensive fallback (should not trigger on well-formed packs): if the
    # row's month is one further back, adjust the year the same way.
    if row_month_num == 12 and expected_month == 1:
        return date(expected_year - 1, 12, 1)
    if row_month_num == expected_month - 1 and expected_month > 1:
        return date(expected_year, expected_month - 1, 1)

    logger.warning(
        "MEI trade row month %r does not match the expected prior-month "
        "%04d-%02d derived from pack %r -- using the row's own month name "
        "with the pack's year as a last resort (flagged for review)",
        month_name, expected_year, expected_month, pack_yyyymm,
    )
    return date(pack_year, row_month_num, 1)


def _parse_mei_pdf_impl(
    pdf_path: Path,
    *,
    pack_yyyymm: str,
) -> list[ParsedMacroPoint]:
    results: list[ParsedMacroPoint] = []
    label = pdf_path.name

    try:
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            import pdfplumber
            with pdfplumber.open(str(pdf_path)) as pdf:
                n_pages = len(pdf.pages)
                if n_pages > _MAX_PAGES:
                    logger.warning(
                        "[%s] Page count %d exceeds cap %d -- rejecting PDF",
                        label, n_pages, _MAX_PAGES,
                    )
                    return results
                # MEI packs have NO /CreationDate (probe-confirmed absent on
                # MEI_202605_e.pdf) -- pdf_creation_date_raw stays None, and
                # the loader's vintage resolution falls back to listing-date
                # or lag-prior imputation for this source.
                pdf_creation_date_raw = pdf.metadata.get("CreationDate")
                full_text = "\n".join((pg.extract_text() or "") for pg in pdf.pages)
    except Exception as exc:
        logger.error("[%s] MEI parse error: %s", label, exc, exc_info=True)
        return results

    common = dict(
        source="CBSL_MEI",
        pdf_creation_date_raw=pdf_creation_date_raw,
        filename_pub_date=None,   # MEI has no URL-embedded publication date
    )

    # --- food imports YoY -------------------------------------------------
    m_food = _MEI_FOOD_IMPORTS_BLOCK_RE.search(full_text)
    if m_food:
        month_name = m_food.group(1)
        yoy_str = m_food.group(4)
        try:
            value = float(yoy_str)
            ref_date = _mei_reference_month(pack_yyyymm, month_name)
            results.append(ParsedMacroPoint(
                series_code=SERIES_FOOD_IMPORTS_YOY,
                reference_date=ref_date,
                value=value,
                **common,
            ))
        except ValueError:
            logger.warning("[%s] Food and Beverages YoY found but not numeric: %r", label, yoy_str)
    else:
        logger.warning(
            "[%s] 'Food and Beverages' Rs.Mn monthly block not found in EXTERNAL "
            "TRADE section -- FOOD_IMPORTS_YOY skipped",
            label,
        )

    # --- policy rate -------------------------------------------------------
    m_opr = _MEI_OPR_RE.search(full_text)
    if m_opr:
        try:
            value = float(m_opr.group(2))  # current-month column
            # Policy rate's reference period is the pack's OWN month (an
            # end-of-month snapshot rate), unlike the 1-month-lagged trade data.
            ref_date = date(int(pack_yyyymm[:4]), int(pack_yyyymm[4:6]), 1)
            results.append(ParsedMacroPoint(
                series_code=SERIES_POLICY_RATE,
                reference_date=ref_date,
                value=value,
                **common,
            ))
        except ValueError:
            logger.warning("[%s] OPR found but not numeric: %r", label, m_opr.group(2))
    else:
        logger.warning(
            "[%s] 'Overnight Policy Rate (OPR)' line not found in INTEREST "
            "RATES section -- POLICY_RATE_OPR skipped",
            label,
        )

    return results


# ---------------------------------------------------------------------------
# Wall-clock timeout wrapper (shared by both parse entry points).
# ---------------------------------------------------------------------------

def _run_with_timeout(fn, *args, **kwargs):
    timeout = _parse_timeout_seconds()
    if timeout <= 0:
        return fn(*args, **kwargs)

    pool = ThreadPoolExecutor(max_workers=1)
    future = pool.submit(fn, *args, **kwargs)
    try:
        result = future.result(timeout=timeout)
        pool.shutdown(wait=False)
        return result
    except FutureTimeoutError:
        label = args[0] if args else "?"
        logger.warning(
            "Parse TIMEOUT after %.1fs on %s -- skipping this PDF "
            "(possible parser-DoS document); ingestion continues.",
            timeout, label,
        )
        pool.shutdown(wait=False)
        return []
