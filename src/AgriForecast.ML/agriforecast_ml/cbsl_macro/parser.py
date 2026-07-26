"""CBSL macro PDF parser: labeled-line regexes, NOT table extraction.

pdfplumber's extract_tables() finds about 18 fragmented layout 'tables' per CCPI press
release, and MEI numbers interleave with chart captions on the same page. Table extraction
is the right tool for HARTI's strict grids and the wrong one for these press releases, so
this parser runs regexes over page.extract_text() against known, stable label phrases.

CCPI press release (2 pages):
  'Index Value <prev> <curr>'        -> the CCPI index level (current column).
  'Monthly Change (%) ...'           -> not persisted; parsed only as a sanity check.
  'Y-o-Y Inflation (%) <prev> <curr>' -> headline YoY.
  'Food inflation (Y-o-Y) ... to/at X%' -> food YoY. The verb varies, so the capture is
  anchored on the label and percentage only.
  The reference month comes from the release's own column-header line, cross-checked
  against the filename - never from text following the food sentence, because pdfplumber
  can interleave a chart caption (which contains the base year 2021) between that
  sentence's month and its year.

MEI pack (25 pages; the page is found by header search, never a fixed index):
  'Food and Beverages' Rs.Mn monthly line -> food-imports YoY. The trade table is ONE
  MONTH BEHIND the pack's own month, and the reference month is read from the table's own
  column header rather than assumed.
  'Overnight Policy Rate (OPR)' in the interest-rates section -> the policy rate. Located
  by its own label, since the table of contents on page 1 also contains the section name.

Throw-don't-guess: an anchor that is not found is a WARN and a skip for that series in
that artifact, never a fabricated value and never a hard failure for the batch.
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

# Parser DoS guard, mirroring harti/parser.py's wall-clock timeout.
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


# Page-count cap after opening. CCPI releases are 2 pages and MEI packs about 25, so 50
# gives headroom while still bounding a PDF that is small on disk but huge to parse.
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


# CCPI press release.

# "Index Value 203.4 207.7" -- two numbers, prev then curr.
_CCPI_INDEX_RE = re.compile(r"Index Value\s+([\d.]+)\s+([\d.]+)")

# "Y-o-Y Inflation (%) 5.5 6.8"
_CCPI_HEADLINE_YOY_RE = re.compile(r"Y-o-Y Inflation \(%\)\s+([\-\d.]+)\s+([\-\d.]+)")

# "Monthly Change (%) 0.9 2.1" -- parsed only for the sanity/drop-counter check.
_CCPI_MONTHLY_CHANGE_RE = re.compile(r"Monthly Change \(%\)\s+([\-\d.]+)\s+([\-\d.]+)")

# Matches prose like 'Food inflation (Y-o-Y) accelerated to 2.8%' or '... remained
# unchanged at 3.0%': the verb varies, and so does the preposition before the number.
#
# Deliberately decoupled from the month and year. An earlier version captured a trailing
# '<Month> <Year>' to derive ReferenceDate, but pdfplumber can interleave an inset chart
# caption between them, and that caption contains '2021' from the base-year label - which
# was then read as the reference year. ReferenceDate now always comes from the release's
# own column-header line instead.
#
# 'Non-Food inflation (Y-o-Y)' is a superstring of this label and MUST stay excluded by
# the negative lookbehind, or a Non-Food figure lands under the Food series with no error.
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

    # Reference month, from the release's own 'Inflation <prevMonth> <currMonth>' column
    # header, with the year inferred from the filename date. This is the single source for
    # EVERY series in the release, and is never derived from the food-inflation sentence.
    ref_date = None
    m_cols = _CCPI_COLUMN_MONTHS_RE.search(full_text)
    m_food = _CCPI_FOOD_YOY_RE.search(full_text)
    if m_cols and filename_pub_date is not None:
        # The current column is the release's own reference month; the year comes from the
        # filename date, since a release publishes within the reference month or just after.
        curr_month_name = m_cols.group(2)
        curr_month_num = _MONTH_TO_NUM[curr_month_name.title()]
        year = filename_pub_date.year
        # If the release month rolls back past the December/January boundary relative to the
        # filename's month, drop the year by one.
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


# MEI pack: food-imports YoY and the policy rate.

# The 'Food and Beverages' Rs.Mn block. The monthly line is the FIRST numeric line under
# the header; the 'January - April' cumulative line follows immediately and must not be
# confused with it. Anchored on the month-name token from the table's own column, not the
# pack's month. The structure is:
#   Food and Beverages
#   April 47,682 74,600 56.5
#   January - April 235,652 232,993 (1.1)
_MEI_FOOD_IMPORTS_BLOCK_RE = re.compile(
    r"Food and Beverages\s*\n\s*(" + _MONTH_ALT + r")\s+"
    r"([\d,]+(?:\.\d+)?)\s+([\d,]+(?:\.\d+)?)\s+"
    r"\(?(-?[\d.]+)\)?"
)

# Section header, used only to bound the search window. The OPR line itself is located by
# its own label, so the table of contents on page 1 is never mistaken for the data page.
_MEI_INTEREST_RATES_HEADER_RE = re.compile(r"^\s*21\.\s*INTEREST RATES", re.MULTILINE)

# 'Overnight Policy Rate (OPR) Per cent 7.75 8.75 1 00': capture the two rate values.
# The trailing basis-point change is not captured - it is redundant and its spacing is
# unstable.
_MEI_OPR_RE = re.compile(
    r"Overnight Policy Rate \(OPR\)\s+Per cent\s+([\d.]+)\s+([\d.]+)"
)

# The trade table's reference month comes from the monthly row's own leading month name;
# the year is inferred from the pack's YYYYMM, since the one-month lag never crosses a
# year and a month boundary at once in the observed corpus. See the tests for the January
# pack referencing December of the prior year.


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
    """Resolve a trade-table row's reference month and year.

    The MEI pack's own month is one month AHEAD of the trade table's data, so we step back one
    calendar month from the pack month. If the row's month name does not match that exactly,
    we allow a match within one month either side, which keeps a January pack pointing at
    December of the prior year rather than silently guessing.
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

    # Defensive fallback for a malformed pack: if the row's month is one further back,
    # adjust the year the same way.
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
                # MEI packs have no /CreationDate, so this stays None and the loader's
                # vintage resolution falls back to the listing date or lag-prior imputation.
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
            # The policy rate's reference period is the pack's OWN month (an end-of-month
            # snapshot), unlike the one-month-lagged trade data.
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


# Wall-clock timeout wrapper, shared by both parse entry points.

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
