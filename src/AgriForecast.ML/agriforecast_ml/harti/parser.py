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

Bitter Gourd / Brinjals name consolidation:
  Pre-2023-02: "Bitter Gourd (Other)"/"Bitter Gourd (Village)" and
  "Brinjals (Other)"/"Brinjals (Village)" may both appear (split varieties).
  From 2023-02-01: HARTI consolidates each pair into a single row
  ("Bitter Gourd", "Brinjals").  We consolidate the pre-split variants to
  the same canonical label as the post-split one.

R2 Step 6.1 widen (ClickUp D-DF6, 2026-07-07):
  _TARGET_CROPS widened from 6 to 20 mappable canonical crops (24 exact
  label keys) and _TARGET_MARKETS/
  _MARKET_HEADER_ALIASES widened from 4 to 10 markets (Kandy, Meegoda,
  Norochchole, Nuwara Eliya, Bandarawela, Veyangoda added), sourced
  entirely from the existing cached corpus (--no-download, zero new
  scraping). See the _TARGET_CROPS and _MARKET_HEADER_ALIASES docstrings
  for full era/spelling evidence.

R2 fruits step 1 (2026-07-16/17):
  Adds 4 per-kg fruit crops (Ambul, Kolikuttu, Seeni, Papaya — see
  _TARGET_FRUITS_PER_KG / _TARGET_FRUITS_PER_FRUIT_SKIP docstrings) from the
  fruit subsection immediately below the vegetables on the same table.
  Fruits flow ONLY through the multi-market PriceObservations path
  (loader.upsert_harti_price_observations, name-keyed CommodityAliases
  resolution) — they are deliberately absent from loader._HARTI_TO_DB_NAME
  / _HARTI_PRODUCT_IDS, so upsert_harti_prices() (the legacy Dambulla-only
  MarketPrices path) can never insert a fruit row.
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

# --------------------------------------------------------------------------
# Parser DoS guard (ClickUp 86cahef8f / S2): wall-clock timeout per PDF.
# --------------------------------------------------------------------------
# A malicious or pathological PDF (deeply nested / degenerate table layout, a
# decompression/parse bomb that slips past the 25 MB download cap because it is
# small-on-disk but expensive-to-parse) could make pdfplumber/pdfminer spin for
# a very long time and stall the whole ingestion pass. We bound each PDF's parse
# with a wall-clock timeout; a PDF that blows the budget is treated exactly like
# any other unparseable PDF (WARN, zero rows, move on) — never a hard failure.
#
# Configurable via AGRI_HARTI_PARSE_TIMEOUT_SECONDS. Default 120s is far more
# than a real ~few-hundred-KB HARTI daily bulletin needs (they parse in well
# under a second each), so a legitimate full-corpus CLI backfill is unaffected.
# 0 (or negative) disables the timeout.
_DEFAULT_PARSE_TIMEOUT_SECONDS = 120.0


def _parse_timeout_seconds() -> float:
    raw = os.getenv("AGRI_HARTI_PARSE_TIMEOUT_SECONDS")
    if raw is None:
        return _DEFAULT_PARSE_TIMEOUT_SECONDS
    try:
        return float(raw)
    except ValueError:
        return _DEFAULT_PARSE_TIMEOUT_SECONDS

# HARTI crop labels we care about.  Keys are the exact strings seen in PDFs;
# value is the canonical HARTI label passed to the loader for CropId mapping.
#
# R2 Step 6.1 widen (ClickUp D-DF6): 6 -> 20 mappable canonical crops
# (24 exact label keys, e.g. 3x Potato / 2x Big Onion variants), full label
# inventory verified against the cached 2,982-PDF corpus (2015-06-22 ->
# 2026-07-01, --no-download only, zero new scraping). English WHOLESALE
# VEGETABLE table only -- fruits (Rs/fruit units) deferred, exactly as
# spec'd. Crop-row matching stays EXACT dict-key lookup (never substring) --
# "Beans" and "Long Beans" are two distinct dict keys and cannot collide.
#
# Pre/post label-consolidation eras (verified via a 131-month, one-PDF-per-
# calendar-month full-history sweep, 2015-06 -> 2026-07):
#   Bitter Gourd:  "(Village)"/"(Other)" split 2015-06-22 .. 2023-01-02,
#                  consolidated to plain "Bitter Gourd" from 2023-02-01.
#   Brinjals:      same pattern -- "(Village)"/"(Other)" split until
#                  2023-01-02, consolidated to plain "Brinjals" (plural)
#                  from 2023-02-01.
#   IMPORTANT: from 2023-02-01 a SEPARATE row literally named "Eggplant"
#   also appears in the table, at the exact row position previously held
#   by "Dambala (Wing Beans)" (verified: 2023-01-02 has "Dambala (Wing
#   Beans)" immediately after "Manioc"; 2023-02-01 has "Eggplant" in that
#   same slot, with comparable Rs/kg price levels). This is HARTI's own
#   relabelling of the Wing Beans row, NOT a second brinjal/aubergine
#   variety -- it is deliberately NOT added to _TARGET_CROPS (would
#   otherwise risk a false alias merge with DB "Eggplant"/VEG000026, a
#   genuinely different commodity). See harti_multimarket_audit.md and the
#   Step 6.1 report for the full trace.
_TARGET_CROPS: dict[str, str] = {
    # --- existing 6 (unchanged) ---
    "Beans":                "Beans",
    "Ladies Fingers":       "Ladies Fingers",
    "Capsicum":             "Capsicum",
    "Bitter Gourd":         "Bitter Gourd",
    "Bitter Gourd (Other)": "Bitter Gourd",   # pre-2023-02 split variant → same slot
    "Luffa":                "Luffa",
    "Snake Gourd":          "Snake Gourd",

    # --- new (R2 Step 6.1), stable spelling corpus-wide (no variant drift
    #     observed in the 131-month sweep) ---
    # NOTE: "Pumpkin" is deliberately EXCLUDED (bulletin has one generic
    # "Pumpkin" row; DB only has "Pumpkin - Big"/"Pumpkin - Malashian",
    # no plain crop -- mapping to either would be guessing a variety). See
    # the Step 6.1 report "unmappable labels" and loader._HARTI_TO_DB_NAME.
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

    # Brinjals: pre/post 2023-02 consolidation (mirrors Bitter Gourd's
    # existing pattern above)
    "Brinjals":          "Brinjals",
    "Brinjals (Village)": "Brinjals",
    "Brinjals (Other)":   "Brinjals",

    # Potatoes: bulletin distinguishes 3 named varieties, each its own DB
    # crop -- NOT consolidated (no plain "Potato" row exists in the
    # bulletin nor in _TARGET_CROPS; "Potato (Imported)" appeared with a
    # space 2021-09-01..2021-10-01 alongside the no-space
    # "Potato(Imported)" seen every other era -- both variants kept).
    "Potato(Imported)":     "Potato (Imported)",
    "Potato (Imported)":    "Potato (Imported)",
    "Potato (Welimada)":    "Potato (Welimada)",
    "Potato (Nuwaraeliya)": "Potato (Nuwaraeliya)",

    # Onions: bulletin only ever carries these 2 rows (Imported / Local) --
    # never a plain "Big Onion" or any "Red Onion" row, so those DB crops
    # stay unmapped (see Step 6.1 report "unmappable labels").
    "B'Onion Imported": "Big Onion Imported",
    "Big-onion Local":  "Big Onion Local",
}

# --------------------------------------------------------------------------
# Fruits (R2 fruits step 1, ClickUp 86caj... 2026-07-16 pre-build analysis):
# per-kg only; per-fruit-unit rows deliberately skipped.
# --------------------------------------------------------------------------
# The same page-2 "Wholesale Prices in Selected Markets" table carries a
# fruit subsection immediately below the vegetables (Banana group: Ambul,
# Kolikuttu, Seeni, Anamalu; then Papaya, Passion Fruits, "Other Fruits
# (Rs/Fruit)": Pineapple/Mango/Woodapple/Avocado/Orange). Unlike the
# vegetable rows above (verified corpus-wide Rs/kg — see loader.py's UNIT
# CONTRACT docstring), fruit rows do NOT share one uniform unit: each row's
# own label carries the unit, either the page-level "(Rs./kg)" default (no
# suffix on the label) or an explicit "(Rs/Fruit)"/"(Rs/Fruits)" override
# baked into the row text itself. A per-fruit price must NEVER be stored as
# a per-kg observation.
#
# Scope (owner-approved, step 1 of a wider plan): only the 4 fruits that
# resolve to an existing per-kg DB crop are targeted (Ambul, Kolikuttu,
# Seeni, Papaya -> Banana - Abul/Kolikuttu/Sini, Papaya via HARTI-source
# CommodityAliases). Every other row in the fruit subsection (Anamalu,
# Passion Fruits, Pineapple, Mango, Woodapple, Avocado, Orange) is a
# per-FRUIT unit corpus-wide with no per-kg DB crop to map to — deliberately
# absent from BOTH dicts below, so those rows fall through the ordinary
# "not a target row" branch exactly like any other non-target vegetable row
# (no dedicated skip-count for them; they were never candidates).
#
# Era evidence (FULL corpus scan, all 2,989 cached PDFs, 2015-06-22 ..
# 2026-07-15 — --no-download only, zero new scraping, harti_cache/; an
# initial 131-sample one-PDF-per-month sweep was cross-checked against this
# exhaustive pass, which is why exact reversion dates below are precise
# rather than month-bucketed):
#   Ambul:      bare "Ambul" 2015-06-22 .. 2021-11-01 (n=1410, implicit
#               Rs/kg default); explicit "Ambul(Rs/Kg)" (NO space before the
#               paren) 2021-11-02 .. 2026-07-15 (n=1403) — a clean one-way
#               flip, no reversions observed. Price levels are continuous
#               across the boundary (e.g. Rs 40-50 in 2015 -> Rs 150-200 by
#               2023, tracking the same inflation trend as the other
#               vegetable rows) — this is a label clarification, not a unit
#               change. Both variants are per-kg and accepted.
#   Kolikuttu:  NOT a clean one-way flip -- two brief REVERSIONS observed
#               (real HARTI publishing inconsistency, not a parser
#               artifact): "Kolikuttu (Rs/Fruits)" (space before paren) is
#               the label 2015-06-22 .. 2020-06-15, briefly flips to bare
#               "Kolikuttu" 2020-06-24 (single sampled occurrence), reverts
#               to "(Rs/Fruits)" 2020-07-01 .. 2020-08-07, then permanently
#               flips to bare "Kolikuttu" 2020-08-10 .. 2021-10-22 -- except
#               a second brief reversion to "(Rs/Fruits)" 2021-10-22 ..
#               2021-10-24, before permanently settling on bare "Kolikuttu"
#               2021-10-25 .. 2026-07-15. Full-corpus totals: "Kolikuttu
#               (Rs/Fruits)" n=1136 (first 2015-06-22, last 2021-10-22);
#               bare "Kolikuttu" n=1677 (first 2020-06-24, last 2026-07-15).
#               "(Rs/Fruits)" is a genuine per-FRUIT unit (price magnitudes
#               consistent with a single banana, e.g. Rs 10-13 in 2015) --
#               intentionally SKIPPED, never stored, on EVERY date it
#               appears (label-driven, not date-driven -- this is exactly
#               why the split-dict design handles the reversions correctly
#               with zero extra logic: each PDF's own row label decides
#               independently, no era/date heuristic is ever consulted).
#               The unit flip is corroborated by the price-level jump (Rs
#               130-150 in 2019 under the per-fruit label -> Rs 340-420 by
#               2023 under the bare/per-kg label, consistent with a genuine
#               per-kg bundle price, not the same quantity merely
#               relabelled). Only the bare "Kolikuttu" label is accepted.
#   Seeni:      always bare "Seeni" corpus-wide (2015-06-22 -> 2026-07-15,
#               n=2813, no suffix ever observed) — implicit Rs/kg default
#               the entire history, always accepted.
#   Papaya:     bare "Papaya" 2015-06-22 .. 2021-11-02 (n=1411, implicit
#               Rs/kg default); explicit "Papaya (Rs/Kg)" (WITH a space
#               before the paren, unlike Ambul's no-space form) 2021-11-03
#               .. 2026-07-15 (n=1402) — a clean one-way flip, no reversions
#               observed. Both variants are per-kg and accepted.
#
# Alias strings below (canonical values) are byte-for-byte what the R2
# fruits-step-1 EF migration seeds into CommodityAliases (Source='HARTI').
_TARGET_FRUITS_PER_KG: dict[str, str] = {
    "Ambul":          "Ambul",
    "Ambul(Rs/Kg)":   "Ambul",
    "Kolikuttu":      "Kolikuttu",
    "Seeni":          "Seeni",
    "Papaya":         "Papaya",
    "Papaya (Rs/Kg)": "Papaya",
}

# Per-FRUIT-unit label variants for the SAME 4 target fruits — positively
# matched (not just "unrecognised") so a skip can be logged with the
# canonical fruit name attached, distinguishing "known per-fruit row,
# deliberately skipped" from "not a target row at all". NEVER added to
# _TARGET_FRUITS_PER_KG — that would be exactly the unit-mismatch bug this
# split is designed to prevent.
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
#
# Thambuttegama / Keppetipola (ClickUp 86cahef44, R1.1 P2) -- both header-
# text-verified across a stratified sample spanning the full corpus
# (harti_multimarket_audit.md Sec 8):
#   Thambuttegama column exists from the very start of the corpus
#   (2015-06-22, the 7-column format already carries "Thambuththegama").
#   Observed header spellings over 11 years:
#     "Thambuththegama"  -- 2015-2016 and again 2025-02 onward (clean format)
#     "T'thegama"         -- 2017-03-08 .. 2017-04-06 only (abbreviated format)
#     "Thambuththegam"    -- 2019-01-01 (a pdfplumber cell-split artifact;
#                             the trailing "a" bleeds into the NEXT cell,
#                             e.g. "a Kappetipola")
#     "ambuththega"       -- 2021-12-01 .. 2021-12-09 (an EARLIER, more
#                             extreme cell-bleed episode than "hambuththegam"
#                             below -- both leading letters bleed backward
#                             into Norochchole, producing "NorochcholTeh")
#     "hambuththegam"     -- 2021-12-10 .. 2024-04 (the leading "T" bleeds
#                             BACKWARD into the Norochchole cell, producing
#                             "NorochcholeT" -- a genuine pdfplumber
#                             table-extraction artifact from missing cell
#                             borders in that era's PDF export, not a HARTI
#                             relabelling)
#   Keppetipola's column is NOT present in the original 7-column format --
#   it is introduced between 2017-03-07 (last observed 7-column PDF) and
#   2017-03-08 (first observed 9-column PDF with a Keppetipola column).
#   Observed header spellings:
#     "Kappetipola"   -- 2017-03-08 .. 2022-06-16 (missing leading "e")
#     "maKappetipola" -- 2021-12-01 .. 2021-12-09 (cell-bleed variant paired
#                         with the "ambuththega" Thambuttegama episode above)
#     "aKappetipola"  -- 2021-12-10 .. 2022-06-16 (cell-bleed variant of the
#                         above, "a" bled in from the Thambuttegama cell)
#     "aKeppetipola"  -- 2023-07-21 .. 2024-04-28 (cell-bleed variant, HARTI
#                         corrected the spelling to "Keppetipola" but the
#                         bleed-through "a" persists)
#     "Keppetipola"   -- 2025-02-02 onward (clean, corrected spelling)
#   Substring-safety verified (script-checked, not just by inspection): none
#   of these new aliases is a substring of any Dambulla/Pettah/Narahenpita
#   alias or of any OTHER market's header text in this bulletin (Kandy,
#   Meegoda, Norochchole/NorochcholeT/NorochcholTeh, Nuwaraeliya,
#   Bandarawela, Veyangoda) -- only intra-market substring relationships
#   exist (e.g. "Kappetipola" is a substring of "aKappetipola", both mapping
#   to the same market), which is safe by construction.
#
# R2 Step 6.1 widen (ClickUp D-DF6): full 4 -> 10 market set. New markets'
# header evidence (same stratified-sample + binary-search method as R1.1 P2,
# against the cached corpus, --no-download only):
#   Kandy       -- "Kandy" -- present from 2015-06-22 (corpus start), never
#                  observed to drift/bleed in any sampled PDF (11 years).
#   Meegoda     -- "Meegoda" clean 2015-2026, plus a truncated/suffixed
#                  "Meegoda*" seen in the single malformed-header PDF
#                  2022-11-26 (the same PDF that fails whole-page detection
#                  per harti_multimarket_audit.md Sec 4 -- kept as a
#                  defensive alias in case a future PDF repeats the pattern
#                  on an otherwise-parseable page).
#   Norochchole -- "Norochchole" clean 2015 .. 2021-11 and again 2025-02
#                  onward; "NorochcholTeh" 2021-12-01..2021-12-09 and
#                  "NorochcholeT" 2021-12-10..2024-04 (both cell-bleed
#                  variants where Thambuttegama's leading letters bleed
#                  backward into this cell -- see the Thambuttegama note
#                  above). IMPORTANT: "Norochchole" (the clean spelling) IS
#                  a substring of "NorochcholeT" (safe, same market) but is
#                  NOT a substring of "NorochcholTeh" (the "e"/"h" are
#                  transposed at the tail) -- that variant needed its own
#                  explicit alias entry, it would otherwise WARN-skip.
#   Nuwara Eliya -- "Nuwaraeliya" (no space) is the standard spelling
#                  2017-03-08 onward (first appears alongside Keppetipola);
#                  "Nuwara Eliya" (space) seen only in the malformed
#                  2022-11-26 PDF page-text (not reachable via the normal
#                  table-header path since that PDF fails whole-page
#                  detection) -- kept as a defensive alias for the same
#                  reason as "Meegoda*" above.
#   Bandarawela -- introduced precisely between 2021-12-01 (absent, 9-column
#                  format) and 2021-12-02 (present, 10-column format) --
#                  binary-searched against the cache. Clean "Bandarawela"
#                  spelling used corpus-wide from introduction, no drift
#                  observed.
#   Veyangoda   -- introduced precisely between 2022-02-21 (absent,
#                  10-column format) and 2022-02-22 (present, 11-column
#                  format) -- binary-searched against the cache (NOTE this
#                  corrects harti_multimarket_audit.md's informal "~2022"
#                  estimate to an exact date). Clean "Veyangoda" spelling
#                  from introduction; a truncated/suffixed "Veyangod*" seen
#                  only in the malformed 2022-11-26 PDF (same defensive
#                  reasoning as Meegoda/Nuwara Eliya above).
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
# unaffected by the new markets appended after it.  Thambuttegama and
# Keppetipola (ClickUp 86cahef44, R1.1 P2) are appended after the R1.1 P1
# trio for the same reason -- existing ordering/back-compat is preserved.
# R2 Step 6.1 (D-DF6) appends the remaining 6 markets (Kandy, Meegoda,
# Norochchole, Nuwara Eliya, Bandarawela, Veyangoda) at the end for the same
# back-compat-by-append reason -- this completes the full 10-market bulletin
# set.
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

    # Run the parse in a worker thread and bound it with a wall-clock timeout.
    # pdfminer/pdfplumber are pure-Python and release the GIL on I/O, so the
    # watchdog stays responsive; on timeout we return empty (exactly like a parse
    # error) and shut the executor down WITHOUT waiting for the runaway worker —
    # using ``with ThreadPoolExecutor(...)`` would block on exit until the slow
    # parse finished, defeating the timeout. The abandoned daemon-ish worker is
    # left to unwind on its own / at interpreter teardown.
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
