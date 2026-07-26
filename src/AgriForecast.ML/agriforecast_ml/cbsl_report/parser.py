"""CBSL Daily Price Report PDF parser (corpus-probed 2026-07-22).

LAYOUT (stable across the probed 2024-2026 corpus): page 2 of the report is a
single commodity table titled "Wholesale and Retail Prices: Selected Food
Commodities - <D Month YYYY>". Five market column-groups —

    Wholesale: Pettah, Dambulla   ·   Retail: Pettah, Dambulla, Narahenpita

— each split into a comparison sub-column (labelled "Yesterday" on Tue-Fri
reports, "Last Friday" + a date on Monday reports) and a "Today" sub-column.
Sections are letter-spaced headers ("V E G E T A B L E S", "O T H E R",
"F R U I T S", "R I C E", "F I S H").

SCOPE (capture-only v1, owner decision 2026-07-22):
  * Sections VEGETABLES / FRUITS / OTHER only. RICE has its own sub-market
    header ("Marandagahamula") and FISH has entirely different markets
    (Peliyagoda/Negombo) — both are out of scope and skipped whole.
  * Rs./kg rows only (the verified unit for produce). Rs./Each / Rs./Nut /
    Rs./Ltr rows (eggs, apples, coconut, oil) are counted + skipped — a
    per-unit conversion factor would be a guess, and the unit contract in
    canonical.py is fail-closed.
  * TODAY values only. The comparison column is yesterday's report re-printed
    (or Friday's, on Mondays) — ingesting it would double-write history we
    already captured from that day's own report.

WHY WORD POSITIONS, NOT TABLE EXTRACTION OR TEXT SPLITTING: pdfplumber's table
mode loses the item labels (merged cells), and the text layer has TWO traps —
(1) a kerning artifact splits every price's first digit(s) from the rest
("2 50.00" = 250.00, "1 ,200.00" = 1200.00), and (2) rows with missing markets
print FEWER than 10 values, so positional splitting cannot tell WHICH markets
the remaining values belong to. Word x-coordinates solve both: split digit
runs are merged by x-adjacency, and each completed value is assigned to the
nearest (market, day) anchor derived from the header words themselves.
"""
from __future__ import annotations

import logging
import re
from dataclasses import dataclass
from datetime import date, datetime
from pathlib import Path
from typing import Sequence

log = logging.getLogger(__name__)

# Parser market names (loader maps these to DB Markets rows BY NAME).
MARKET_KEYS: tuple[str, ...] = (
    "Pettah (wholesale)",
    "Dambulla (wholesale)",
    "Pettah (retail)",
    "Dambulla (retail)",
    "Narahenpita (retail)",
)

# Sections parsed (v1 scope). RICE / FISH deliberately excluded — see module doc.
_PARSED_SECTIONS = {"VEGETABLES", "FRUITS", "OTHER"}
_KNOWN_SECTIONS = {"VEGETABLES", "FRUITS", "OTHER", "RICE", "FISH"}

_UNIT_RE = re.compile(r"^Rs\.?/\w+$")
_PARSED_UNIT = "Rs./kg"

# A completed price value: digits (with optional thousands commas) + 2 decimals.
_VALUE_RE = re.compile(r"^\d{1,3}(?:,\d{3})*\.\d{2}$|^\d+\.\d{2}$")
_NA_TOKEN = "n.a."

_TITLE_RE = re.compile(
    r"Wholesale and Retail Prices.*-\s*(\d{1,2})\s+([A-Za-z]+)\s+(\d{4})"
)

# Max horizontal distance (pt) between a value's centre and its claimed anchor.
# Sub-columns sit ~45-55pt apart in the probed corpus; half that is a safe snap
# radius — anything farther is a layout regression and must be skipped loudly.
_MAX_ANCHOR_SNAP_PT = 30.0


@dataclass
class ParsedCbslPrice:
    date_str: str                 # report date "YYYY-MM-DD" (the TODAY column's date)
    cbsl_label: str               # verbatim item label, e.g. "Beans", "Red Onion (Local)"
    unit_raw: str                 # always "Rs./kg" in emitted rows (v1 scope filter)
    market_name: str              # one of MARKET_KEYS
    price: float                  # TODAY price, LKR/kg
    pdf_creation_date_raw: str | None = None  # PDF /CreationDate ("D:...") for AsOfUtc


@dataclass
class _Word:
    text: str
    x0: float
    x1: float
    top: float

    @property
    def centre(self) -> float:
        return (self.x0 + self.x1) / 2.0


def _group_lines(words: list[_Word], tol: float = 3.0) -> list[list[_Word]]:
    """Group words into visual lines by their top coordinate (ascending)."""
    lines: list[list[_Word]] = []
    for w in sorted(words, key=lambda w: (w.top, w.x0)):
        if lines and abs(lines[-1][0].top - w.top) <= tol:
            lines[-1].append(w)
        else:
            lines.append([w])
    for line in lines:
        line.sort(key=lambda w: w.x0)
    return lines


def _section_of(line: list[_Word]) -> str | None:
    """A letter-spaced section header ("V E G E T A B L E S") or None."""
    joined = "".join(w.text for w in line)
    if joined.isalpha() and joined.isupper() and len(line) >= 4:
        return joined
    return None


def _parse_title_date(text: str) -> str | None:
    m = _TITLE_RE.search(text)
    if not m:
        return None
    try:
        d = datetime.strptime(f"{m.group(1)} {m.group(2)} {m.group(3)}", "%d %B %Y").date()
    except ValueError:
        return None
    return d.isoformat()


def _merge_values(tokens: list[_Word]) -> list[tuple[str, float]]:
    """Merge kerning-split numeric words into complete values.

    Returns [(value_text, x_centre)] where value_text is a normalised number
    ("250.00", "1200.00") or the literal "n.a.". A run of digit words is
    accumulated until the combined text (commas stripped) matches _VALUE_RE;
    an accumulation that never completes is dropped with a WARN (layout
    regression guard — never guessed into a number).
    """
    out: list[tuple[str, float]] = []
    acc: list[_Word] = []

    def flush_incomplete():
        if acc:
            log.warning("CBSL parser: dropping incomplete numeric run %r",
                        " ".join(w.text for w in acc))
            acc.clear()

    for w in tokens:
        if w.text == _NA_TOKEN:
            flush_incomplete()
            out.append((_NA_TOKEN, w.centre))
            continue
        acc.append(w)
        combined = "".join(t.text for t in acc).replace(",", "")
        if _VALUE_RE.match(combined):
            x_centre = (acc[0].x0 + acc[-1].x1) / 2.0
            out.append((combined, x_centre))
            acc.clear()
        elif not re.fullmatch(r"[\d,\.]+", combined):
            # Non-numeric junk broke the run — drop it, keep scanning.
            flush_incomplete()
    flush_incomplete()
    return out


def parse_pdf(pdf_path: Path, date_str_hint: str | None = None) -> list[ParsedCbslPrice]:
    """Parse one CBSL Daily Price Report PDF into TODAY price rows."""
    import pdfplumber

    rows: list[ParsedCbslPrice] = []
    with pdfplumber.open(pdf_path) as pdf:
        creation_raw = (pdf.metadata or {}).get("CreationDate")

        # Locate the commodity-table page by its title (page 2 in the probed
        # corpus, but scan defensively).
        page = None
        title_date: str | None = None
        for p in pdf.pages:
            text = p.extract_text() or ""
            if "Wholesale and Retail Prices" in text:
                page = p
                title_date = _parse_title_date(text)
                break
        if page is None:
            log.warning("CBSL parser: %s has no commodity-table page — skipping file",
                        pdf_path.name)
            return rows

        date_str = title_date or date_str_hint
        if date_str is None:
            log.warning("CBSL parser: %s: report date unreadable and no hint — skipping file",
                        pdf_path.name)
            return rows
        if title_date and date_str_hint and title_date != date_str_hint:
            # Filename date vs in-report date disagree — trust the report itself.
            log.warning("CBSL parser: %s: filename says %s but the report titles itself %s "
                        "— using the report's own date", pdf_path.name, date_str_hint, title_date)

        words = [
            _Word(w["text"], float(w["x0"]), float(w["x1"]), float(w["top"]))
            for w in page.extract_words()
        ]
        lines = _group_lines(words)

        # Header anchors.
        # Market-group header: the line carrying Pettah/Dambulla/.../Narahenpita.
        market_line = None
        for line in lines:
            texts = [w.text for w in line]
            if texts.count("Pettah") == 2 and texts.count("Dambulla") == 2 \
                    and "Narahenpita" in texts:
                market_line = line
                break
        if market_line is None:
            log.warning("CBSL parser: %s: market header line not found — skipping file",
                        pdf_path.name)
            return rows
        market_words = [w for w in market_line
                        if w.text in ("Pettah", "Dambulla", "Narahenpita")]
        if len(market_words) != 5:
            log.warning("CBSL parser: %s: expected 5 market header words, found %d — skipping",
                        pdf_path.name, len(market_words))
            return rows

        # Day anchors: the 'Today' words give the today sub-column centres, and 'Yesterday'
        # (Tue-Fri) or 'Friday' (Monday reports) give the comparison ones. Five of each.
        today_words = [w for line in lines for w in line if w.text == "Today"]
        comp_words = [w for line in lines for w in line
                      if w.text in ("Yesterday", "Friday")]
        if len(today_words) != 5 or len(comp_words) < 5:
            log.warning(
                "CBSL parser: %s: day anchors off (today=%d, comparison=%d; want 5/5) "
                "— skipping file rather than guessing columns",
                pdf_path.name, len(today_words), len(comp_words))
            return rows

        # (market, is_today) anchor list: each day word claims its nearest
        # market header word.
        anchors: list[tuple[float, str, bool]] = []
        for w in sorted(today_words, key=lambda w: w.centre):
            mkt = min(market_words, key=lambda m: abs(m.centre - w.centre))
            anchors.append((w.centre, mkt.text, True))
        for w in sorted(comp_words, key=lambda w: w.centre)[:5]:
            mkt = min(market_words, key=lambda m: abs(m.centre - w.centre))
            anchors.append((w.centre, mkt.text, False))

        # Market header word -> MARKET_KEYS name, by left-to-right order:
        # Pettah(W), Dambulla(W), Pettah(R), Dambulla(R), Narahenpita(R).
        market_names_by_word = {
            id(mw): MARKET_KEYS[i]
            for i, mw in enumerate(sorted(market_words, key=lambda m: m.centre))
        }
        anchor_names: list[tuple[float, str, bool]] = []
        for centre, mkt_text, is_today in anchors:
            mw = min(
                (m for m in market_words if m.text == mkt_text),
                key=lambda m: abs(m.centre - centre),
            )
            anchor_names.append((centre, market_names_by_word[id(mw)], is_today))

        # Item rows.
        section: str | None = None
        for line in lines:
            sec = _section_of(line)
            if sec is not None:
                section = sec if sec in _KNOWN_SECTIONS else section
                continue
            if section not in _PARSED_SECTIONS:
                continue

            unit_idx = next(
                (i for i, w in enumerate(line) if _UNIT_RE.match(w.text)), None)
            if unit_idx is None or unit_idx == 0:
                continue  # not an item row (sub-headers, footnotes, market lines)
            unit_raw = line[unit_idx].text
            label = " ".join(w.text for w in line[:unit_idx]).strip()
            if not label:
                continue
            if unit_raw != _PARSED_UNIT:
                log.info("CBSL parser: [%s] %r: unit %s out of scope — skipped",
                         date_str, label, unit_raw)
                continue

            for value_text, x_centre in _merge_values(line[unit_idx + 1:]):
                if value_text == _NA_TOKEN:
                    continue
                centre_dist, (a_centre, mkt_name, is_today) = min(
                    ((abs(a[0] - x_centre), a) for a in anchor_names),
                    key=lambda t: t[0],
                )
                if centre_dist > _MAX_ANCHOR_SNAP_PT:
                    log.warning(
                        "CBSL parser: [%s] %r: value %s at x=%.0f is %.0fpt from the "
                        "nearest column anchor (max %.0f) — layout drift, value skipped",
                        date_str, label, value_text, x_centre, centre_dist,
                        _MAX_ANCHOR_SNAP_PT)
                    continue
                if not is_today:
                    continue  # comparison column = a prior report's data
                rows.append(ParsedCbslPrice(
                    date_str=date_str,
                    cbsl_label=label,
                    unit_raw=unit_raw,
                    market_name=mkt_name,
                    price=float(value_text),
                    pdf_creation_date_raw=str(creation_raw) if creation_raw else None,
                ))

    return rows


def parse_many(
    cached_pdfs: Sequence[tuple[str, Path]], *, log_every: int = 50
) -> list[ParsedCbslPrice]:
    """Parse a batch of (date_str, path) PDFs; per-file failures are isolated."""
    out: list[ParsedCbslPrice] = []
    for i, (date_str, path) in enumerate(cached_pdfs, 1):
        try:
            out.extend(parse_pdf(path, date_str_hint=date_str))
        except Exception:
            log.exception("CBSL parser: %s failed to parse — skipping file", path.name)
        if i % log_every == 0:
            log.info("CBSL parser: %d/%d files parsed", i, len(cached_pdfs))
    return out
