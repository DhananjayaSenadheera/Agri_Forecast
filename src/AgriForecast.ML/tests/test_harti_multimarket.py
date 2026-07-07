"""
AgriForecast ML — HARTI multi-market parser/loader tests (R1.1 P1,
ClickUp 86cahef3e; extended R1.1 P2, ClickUp 86cahef44 for
Thambuttegama/Keppetipola).

All tests are hermetic: synthetic in-memory table fixtures (list-of-lists,
matching pdfplumber's extract_tables() shape), no network, no DB. Mirrors
the style of test_downloader_cap.py (pure unit tests with mocked
collaborators) and test_harti_splice.py (dataclass-driven loader tests).

Coverage:
  TestLocateMarketColumn        — header-located columns map to the right
                                   market; missing header -> None, not a
                                   positional guess.
  TestColumnOrderShuffle        — R1 regression: a column-order-shuffled
                                   table still maps each market correctly
                                   (extended to all 5 target markets).
  TestLocateArrivalsColumn      — arrivals column found when header present;
                                   None (not an error) when absent.
  TestParsePdfMultiMarket       — parse_pdf()-level integration using a
                                   monkeypatched _find_english_veg_page, i.e.
                                   no real PDF I/O: multi-market rows emitted,
                                   partial-market skip warns but continues,
                                   total-column-loss skips the whole PDF.
  TestMarketNameResolution      — loader market_name -> DB Market: happy
                                   path resolves by name (no hardcoded GUID),
                                   a miss WARN-skips rows without inventing
                                   a market.
  TestAsOfUtc                   — PDF /CreationDate parses to the correct
                                   UTC instant (including delimiter-less
                                   "+0530"-style offsets); missing/garbage
                                   falls back to a conservative-LATE vintage
                                   (ObservedDate+1 06:00 Sri Lanka time),
                                   never same-day end-of-day and never
                                   earlier than ObservedDate -- leakage guard.
  TestDambullaBackCompat        — upsert_harti_prices() filters to
                                   Dambulla-only, preserving the legacy
                                   MarketPrices contract untouched.
  TestThambuttegamaKeppetipola  — R1.1 P2: new-market header aliases
                                   (real spelling variants observed across
                                   the corpus, incl. cell-bleed artifacts),
                                   substring-safety, missing-column WARN
                                   behaviour, and loader market-name
                                   resolution for both new markets.
"""
from __future__ import annotations

import sys
import uuid
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import MagicMock

import pytest

# ---------------------------------------------------------------------------
# Path setup
# ---------------------------------------------------------------------------
ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml.harti import parser as harti_parser  # noqa: E402
from agriforecast_ml.harti import loader as harti_loader  # noqa: E402
from agriforecast_ml.harti.parser import ParsedPrice  # noqa: E402


# ===========================================================================
# Synthetic table fixtures (shape mirrors pdfplumber's extract_tables()[0])
# ===========================================================================

def _standard_table(*, include_narahenpita=False, include_arrivals=False):
    """A 2019-format-like table: header rows + crop rows.

    Column order: [Variety, Pettah, Kandy, Dambulla, Meegoda, ...] — matches
    the real corpus layout observed in harti_cache/harti_2019-01-01.pdf.
    """
    header1 = ["Variety", "1/1/2019", "1/1/2019", "1/1/2019", "31/12/2018"]
    header2 = [None, "Pettah\nMarket", "Kandy\nMarket", "Dambulla\nMarket", "Meegoda\nMarket"]
    if include_narahenpita:
        header1.append("1/1/2019")
        header2.append("Narahenpita\nMarket")
    if include_arrivals:
        header1.append("1/1/2019")
        header2.append("Arrivals\n(Kg)")
    header3 = ["" for _ in header1]

    def _row(label, *cells):
        r = [label] + list(cells)
        while len(r) < len(header1):
            r.append("")
        return r

    rows = [
        header1,
        header2,
        header3,
        _row("Beans", "80.00- 100.00", "-", "100.00 -120.00", "160.00 -165.00",
             *(["90.00 -110.00"] if include_narahenpita else []),
             *(["1500"] if include_arrivals else [])),
        _row("Capsicum", "250.00- 280.00", "-", "300.00 -320.00", "-",
             *(["270.00 -290.00"] if include_narahenpita else []),
             *(["800"] if include_arrivals else [])),
    ]
    return rows


def _table_missing_dambulla():
    """Table where Dambulla's header is missing entirely (renamed to
    something unregistered) — Dambulla column must NOT be located."""
    header2 = [None, "Pettah\nMarket", "Kandy\nMarket", "SomeNewMarket\nMarket", "Meegoda\nMarket"]
    header1 = ["Variety", "1/1/2019", "1/1/2019", "1/1/2019", "31/12/2018"]
    header3 = ["" for _ in header1]
    rows = [
        header1, header2, header3,
        ["Beans", "80.00- 100.00", "-", "999.00 -999.00", "160.00 -165.00"],
    ]
    return rows


def _table_only_pettah():
    """Table where only Pettah's header is present (Dambulla/Narahenpita
    absent) — used to prove partial-market skip doesn't kill the whole PDF."""
    header1 = ["Variety", "1/1/2019", "1/1/2019"]
    header2 = [None, "Peliyagoda\nMarket", "Kandy\nMarket"]
    header3 = ["", "", ""]
    rows = [
        header1, header2, header3,
        ["Beans", "80.00- 100.00", "-"],
    ]
    return rows


def _table_no_target_markets():
    """No target market header present at all -- whole-PDF skip case.

    R2 Step 6.1 note: "Kandy" is now one of the 10 target markets (was
    non-target noise pre-6.1), so this fixture uses two genuinely
    unregistered market names instead."""
    header1 = ["Variety", "1/1/2019", "1/1/2019"]
    header2 = [None, "SomeMarket\nMarket", "AnotherMarket\nMarket"]
    header3 = ["", "", ""]
    rows = [
        header1, header2, header3,
        ["Beans", "80.00- 100.00", "-"],
    ]
    return rows


# ===========================================================================
# 1. _locate_market_column — header-located columns map to the right market
# ===========================================================================

class TestLocateMarketColumn:
    def test_dambulla_located_by_header(self):
        table = _standard_table()
        col = harti_parser._locate_market_column(table, "Dambulla")
        assert col == 3, "Dambulla header is column 3 in the standard fixture"

    def test_pettah_located_by_header(self):
        table = _standard_table()
        col = harti_parser._locate_market_column(table, "Pettah")
        assert col == 1, "Pettah header is column 1 in the standard fixture"

    def test_pettah_located_via_peliyagoda_alias(self):
        """R1.1: 'Pettah' renamed to 'Peliyagoda' in the bulletin header from
        ~2021 -- must still resolve to the Pettah market via the documented
        alias list, not a positional fallback."""
        table = _table_only_pettah()
        col = harti_parser._locate_market_column(table, "Pettah")
        assert col == 1

    def test_pettah_located_via_typo_alias(self):
        """The observed 'Peliyagod' typo (missing trailing 'a') must also
        resolve via the documented alias list."""
        header1 = ["Variety", "1/1/2026"]
        header2 = [None, "Peliyagod\nMarket"]
        table = [header1, header2, ["", ""], ["Beans", "100-120"]]
        col = harti_parser._locate_market_column(table, "Pettah")
        assert col == 1

    def test_missing_header_returns_none_not_positional_guess(self):
        """R1 core contract: if the header can't be located, return None —
        never fall back to a hardcoded column index."""
        table = _table_missing_dambulla()
        col = harti_parser._locate_market_column(table, "Dambulla")
        assert col is None, (
            "Dambulla header absent -- must return None, not guess a column"
        )

    def test_narahenpita_missing_returns_none(self):
        table = _standard_table(include_narahenpita=False)
        col = harti_parser._locate_market_column(table, "Narahenpita")
        assert col is None

    def test_narahenpita_located_when_present(self):
        table = _standard_table(include_narahenpita=True)
        col = harti_parser._locate_market_column(table, "Narahenpita")
        assert col == 5

    def test_dambulla_col_index_backcompat_wrapper(self):
        """_dambulla_col_index() must still exist and behave identically to
        _locate_market_column(table, 'Dambulla') for existing callers."""
        table = _standard_table()
        assert harti_parser._dambulla_col_index(table) == harti_parser._locate_market_column(table, "Dambulla")

    def test_unregistered_market_name_falls_back_to_literal_match(self):
        """A market name with no alias-list entry still does exact-substring
        header matching (not silently unsupported)."""
        header2 = [None, "SomeMarket\nMarket"]
        header1 = ["Variety", "1/1/2019"]
        table = [header1, header2, ["", ""], ["Beans", "100-120"]]
        col = harti_parser._locate_market_column(table, "SomeMarket")
        assert col == 1


# ===========================================================================
# 2. COLUMN-ORDER SHUFFLE — R1 regression test
# ===========================================================================

class TestColumnOrderShuffle:
    """The R1 risk: a column-order-shuffled table must still map each market
    to its own correct column, never cross-contaminating prices."""

    def _shuffled_table(self):
        # Deliberately different order from every other fixture in this file:
        # [Variety, Meegoda, Dambulla, Narahenpita, Kandy, Pettah]
        header1 = ["Variety", "d1", "d1", "d1", "d1", "d1"]
        header2 = [None, "Meegoda\nMarket", "Dambulla\nMarket", "Narahenpita\nMarket",
                   "Kandy\nMarket", "Pettah\nMarket"]
        header3 = ["" for _ in header1]
        rows = [
            header1, header2, header3,
            ["Beans", "160.00 -165.00", "100.00 -120.00", "90.00 -110.00", "-", "80.00- 100.00"],
        ]
        return rows

    def test_each_market_maps_to_its_own_column_regardless_of_order(self):
        table = self._shuffled_table()
        dambulla_col = harti_parser._locate_market_column(table, "Dambulla")
        pettah_col = harti_parser._locate_market_column(table, "Pettah")
        narahenpita_col = harti_parser._locate_market_column(table, "Narahenpita")

        assert dambulla_col == 2
        assert pettah_col == 5
        assert narahenpita_col == 3
        # All distinct -- no column collision
        assert len({dambulla_col, pettah_col, narahenpita_col}) == 3

    def test_shuffled_table_prices_do_not_cross_contaminate(self):
        """End-to-end: reading each located column must give each market its
        own distinct price, not another market's number."""
        table = self._shuffled_table()
        dambulla_col = harti_parser._locate_market_column(table, "Dambulla")
        pettah_col = harti_parser._locate_market_column(table, "Pettah")
        narahenpita_col = harti_parser._locate_market_column(table, "Narahenpita")

        beans_row = table[3]
        dambulla_price = harti_parser._parse_price_cell(beans_row[dambulla_col])
        pettah_price = harti_parser._parse_price_cell(beans_row[pettah_col])
        narahenpita_price = harti_parser._parse_price_cell(beans_row[narahenpita_col])

        assert dambulla_price == (100.0, 120.0)
        assert pettah_price == (80.0, 100.0)
        assert narahenpita_price == (90.0, 110.0)
        # Pairwise distinct -- a column swap would collapse two of these
        assert len({dambulla_price, pettah_price, narahenpita_price}) == 3

    def _shuffled_table_five_markets(self):
        """R1.1 P2 (ClickUp 86cahef44) extension: same shuffle risk, now
        across all 5 target markets, deliberately reordered and interleaved
        with non-target columns (Kandy, Meegoda) so no market sits at a
        'suspiciously convenient' fixed offset.
        Order: [Variety, Keppetipola, Meegoda, Dambulla, Thambuttegama,
                Narahenpita, Kandy, Pettah]
        """
        header1 = ["Variety", "d1", "d1", "d1", "d1", "d1", "d1", "d1"]
        header2 = [
            None,
            "Keppetipola\nMarket", "Meegoda\nMarket", "Dambulla\nMarket",
            "Thambuththegama\nMarket", "Narahenpita\nMarket", "Kandy\nMarket",
            "Pettah\nMarket",
        ]
        header3 = ["" for _ in header1]
        rows = [
            header1, header2, header3,
            ["Beans",
             "70.00 -80.00",       # Keppetipola
             "160.00 -165.00",     # Meegoda (non-target)
             "100.00 -120.00",     # Dambulla
             "60.00 -65.00",       # Thambuttegama
             "90.00 -110.00",      # Narahenpita
             "-",                  # Kandy (non-target)
             "80.00- 100.00"],     # Pettah
        ]
        return rows

    def test_five_markets_map_to_distinct_columns_regardless_of_order(self):
        table = self._shuffled_table_five_markets()
        cols = {
            m: harti_parser._locate_market_column(table, m)
            for m in ("Dambulla", "Pettah", "Narahenpita", "Thambuttegama", "Keppetipola")
        }
        assert cols == {
            "Keppetipola": 1,
            "Dambulla": 3,
            "Thambuttegama": 4,
            "Narahenpita": 5,
            "Pettah": 7,
        }
        # All 5 distinct -- no column collision across any pair
        assert len(set(cols.values())) == 5

    def test_five_markets_prices_are_pairwise_distinct_no_cross_contamination(self):
        """End-to-end: all 5 markets' Beans prices must be mutually distinct
        -- a column swap between any pair would collapse two of these."""
        table = self._shuffled_table_five_markets()
        beans_row = table[3]
        prices = {}
        for market in ("Dambulla", "Pettah", "Narahenpita", "Thambuttegama", "Keppetipola"):
            col = harti_parser._locate_market_column(table, market)
            prices[market] = harti_parser._parse_price_cell(beans_row[col])

        assert prices == {
            "Keppetipola": (70.0, 80.0),
            "Dambulla": (100.0, 120.0),
            "Thambuttegama": (60.0, 65.0),
            "Narahenpita": (90.0, 110.0),
            "Pettah": (80.0, 100.0),
        }
        # Pairwise distinct across all 5 -- proves no two markets ever read
        # the same cell.
        assert len(set(prices.values())) == 5


# ===========================================================================
# 3. _locate_arrivals_column — detect-don't-hardcode, absent != error
# ===========================================================================

class TestLocateArrivalsColumn:
    def test_arrivals_column_found_when_present(self):
        table = _standard_table(include_arrivals=True)
        col = harti_parser._locate_arrivals_column(table)
        assert col is not None
        assert "Arrivals" in str(table[1][col])

    def test_arrivals_column_none_when_absent(self):
        table = _standard_table(include_arrivals=False)
        col = harti_parser._locate_arrivals_column(table)
        assert col is None, "No arrivals header present -- must be None, not an error"

    def test_volume_header_variant_also_detected(self):
        header1 = ["Variety", "d1", "d1"]
        header2 = [None, "Dambulla\nMarket", "Volume\n(Kg)"]
        table = [header1, header2, ["", "", ""], ["Beans", "100-120", "500"]]
        col = harti_parser._locate_arrivals_column(table)
        assert col == 2

    def test_parse_arrivals_cell_numeric(self):
        assert harti_parser._parse_arrivals_cell("1500") == 1500.0
        assert harti_parser._parse_arrivals_cell("1,500") == 1500.0

    def test_parse_arrivals_cell_empty_is_none(self):
        assert harti_parser._parse_arrivals_cell("-") is None
        assert harti_parser._parse_arrivals_cell("") is None
        assert harti_parser._parse_arrivals_cell(None) is None


# ===========================================================================
# 4. parse_pdf() multi-market integration (monkeypatched page detection,
#    no real PDF I/O)
# ===========================================================================

class TestParsePdfMultiMarket:
    """Exercise parse_pdf() end-to-end against synthetic tables by
    monkeypatching _find_english_veg_page — no real pdfplumber.open() call,
    fully hermetic."""

    def _run_parse_pdf(self, monkeypatch, table, tmp_path, *, creation_date="D:20190102101632+05'30'"):
        fake_pdf_path = tmp_path / "fake.pdf"
        fake_pdf_path.write_bytes(b"%PDF-1.4 fake")

        class _FakePdf:
            def __init__(self):
                self.pages = [object()]
                self.metadata = {"CreationDate": creation_date} if creation_date else {}

            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

        monkeypatch.setattr(
            harti_parser, "_find_english_veg_page", lambda pdf: (0, table)
        )

        import pdfplumber
        monkeypatch.setattr(pdfplumber, "open", lambda *_a, **_k: _FakePdf())

        return harti_parser.parse_pdf(fake_pdf_path, "2019-01-01")

    def test_all_locatable_markets_emit_rows(self, monkeypatch, tmp_path):
        """R2 Step 6.1 note: _standard_table() also carries a 'Meegoda'
        header column (with real Beans/Capsicum data) -- since Meegoda is
        now one of the 10 target markets (was non-target noise pre-6.1),
        it legitimately emits rows here too."""
        table = _standard_table(include_narahenpita=True)
        rows = self._run_parse_pdf(monkeypatch, table, tmp_path)
        markets = {r.market_name for r in rows}
        assert markets == {"Dambulla", "Pettah", "Narahenpita", "Meegoda"}

    def test_partial_market_missing_others_still_parse(self, monkeypatch, tmp_path, caplog):
        """Narahenpita absent (as in real 2019-format bulletins) -> WARN-skip
        for that market only; Dambulla/Pettah/Meegoda still parse normally
        (Meegoda is target-market noise baked into _standard_table(), see
        the note on test_all_locatable_markets_emit_rows above)."""
        table = _standard_table(include_narahenpita=False)
        rows = self._run_parse_pdf(monkeypatch, table, tmp_path)
        markets = {r.market_name for r in rows}
        assert markets == {"Dambulla", "Pettah", "Meegoda"}
        assert "Narahenpita" not in markets
        assert any("Narahenpita" in rec.message and "not located" in rec.message
                    for rec in caplog.records)

    def test_no_target_market_located_skips_whole_pdf(self, monkeypatch, tmp_path, caplog):
        table = _table_no_target_markets()
        rows = self._run_parse_pdf(monkeypatch, table, tmp_path)
        assert rows == [], "No target market column located -- whole PDF must be skipped"
        assert any("NO target market column" in rec.message for rec in caplog.records)

    def test_missing_dambulla_header_no_positional_fallback(self, monkeypatch, tmp_path, caplog):
        """R1 core: Dambulla's header renamed/missing -- must WARN-skip
        Dambulla specifically, never silently read another market's column
        as if it were Dambulla."""
        table = _table_missing_dambulla()
        rows = self._run_parse_pdf(monkeypatch, table, tmp_path)
        markets = {r.market_name for r in rows}
        assert "Dambulla" not in markets
        # Pettah is still locatable in this fixture -- proves partial skip works
        assert "Pettah" in markets
        assert any("Dambulla" in rec.message and "not located" in rec.message
                    for rec in caplog.records)

    def test_arrivals_populated_when_column_present(self, monkeypatch, tmp_path):
        table = _standard_table(include_arrivals=True)
        rows = self._run_parse_pdf(monkeypatch, table, tmp_path)
        beans_dambulla = next(r for r in rows if r.harti_label == "Beans" and r.market_name == "Dambulla")
        assert beans_dambulla.arrivals_kg == 1500.0

    def test_arrivals_null_when_column_absent(self, monkeypatch, tmp_path):
        table = _standard_table(include_arrivals=False)
        rows = self._run_parse_pdf(monkeypatch, table, tmp_path)
        for r in rows:
            assert r.arrivals_kg is None

    def test_pdf_creation_date_carried_through_to_parsed_price(self, monkeypatch, tmp_path):
        table = _standard_table()
        rows = self._run_parse_pdf(monkeypatch, table, tmp_path, creation_date="D:20190102101632+05'30'")
        assert all(r.pdf_creation_date_raw == "D:20190102101632+05'30'" for r in rows)

    def test_missing_creation_date_metadata_is_none_not_error(self, monkeypatch, tmp_path):
        table = _standard_table()
        rows = self._run_parse_pdf(monkeypatch, table, tmp_path, creation_date=None)
        assert rows, "Rows should still parse even with no PDF metadata"
        assert all(r.pdf_creation_date_raw is None for r in rows)

    def test_dambulla_rows_unaffected_by_new_markets_values(self, monkeypatch, tmp_path):
        """Dambulla's own parsed prices must be identical whether or not
        Narahenpita/arrivals columns are present -- multi-market extraction
        must not perturb the pre-existing Dambulla-only values."""
        table_without = _standard_table(include_narahenpita=False, include_arrivals=False)
        table_with = _standard_table(include_narahenpita=True, include_arrivals=True)

        rows_without = self._run_parse_pdf(monkeypatch, table_without, tmp_path)
        rows_with = self._run_parse_pdf(monkeypatch, table_with, tmp_path)

        dambulla_without = {
            r.harti_label: (r.min_price, r.max_price)
            for r in rows_without if r.market_name == "Dambulla"
        }
        dambulla_with = {
            r.harti_label: (r.min_price, r.max_price)
            for r in rows_with if r.market_name == "Dambulla"
        }
        assert dambulla_without == dambulla_with


# ===========================================================================
# 5. MARKET-NAME RESOLUTION (loader)
# ===========================================================================

class TestMarketNameResolution:
    """upsert_harti_price_observations() resolves parser market_name -> DB
    MarketId BY NAME (never a hardcoded GUID); an unresolved name is
    WARN-skipped, not invented."""

    def _fake_market_map(self, names=("Dambulla", "Pettah", "Narahenpita")):
        return {
            name: uuid.UUID(f"aaaaaaaa-0000-0000-0000-{i:012d}")
            for i, name in enumerate(names)
        }

    def test_all_three_markets_resolve_and_insert(self):
        fake_map = self._fake_market_map()
        rows = [
            ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla"),
            ParsedPrice("2019-01-01", "Beans", "80-100", 80.0, 100.0, market_name="Pettah"),
            ParsedPrice("2019-01-01", "Beans", "90-110", 90.0, 110.0, market_name="Narahenpita"),
        ]
        original = harti_loader._build_market_map
        harti_loader._build_market_map = lambda _: fake_map
        try:
            result = harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)
        finally:
            harti_loader._build_market_map = original

        assert result["inserted"] == 3
        assert result["skipped_no_market"] == 0

    def test_unresolved_market_name_skipped_not_invented(self, caplog):
        """A market name with no DB row (e.g. Narahenpita not yet seeded in
        some environment) must be WARN-skipped -- never silently mapped to
        an existing market or a synthesised GUID."""
        fake_map = self._fake_market_map(names=("Dambulla", "Pettah"))  # Narahenpita absent
        rows = [
            ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla"),
            ParsedPrice("2019-01-01", "Beans", "90-110", 90.0, 110.0, market_name="Narahenpita"),
        ]
        original = harti_loader._build_market_map
        harti_loader._build_market_map = lambda _: fake_map
        try:
            result = harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)
        finally:
            harti_loader._build_market_map = original

        assert result["inserted"] == 1
        assert result["skipped_no_market"] == 1
        assert any("did not resolve" in rec.message for rec in caplog.records)

    def test_market_map_cached_per_run_not_per_row(self):
        """_build_market_map must be invoked exactly once per
        upsert_harti_price_observations() call, not once per row (cache
        per run, per the task spec)."""
        fake_map = self._fake_market_map()
        call_count = {"n": 0}

        def _counting_build_market_map(_engine):
            call_count["n"] += 1
            return fake_map

        rows = [
            ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla"),
            ParsedPrice("2019-01-02", "Capsicum", "200-250", 200.0, 250.0, market_name="Pettah"),
            ParsedPrice("2019-01-03", "Snake Gourd", "150-180", 150.0, 180.0, market_name="Narahenpita"),
        ]
        original = harti_loader._build_market_map
        harti_loader._build_market_map = _counting_build_market_map
        try:
            harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)
        finally:
            harti_loader._build_market_map = original

        assert call_count["n"] == 1, (
            "_build_market_map must be called once per run, got %d calls" % call_count["n"]
        )

    def test_parser_market_to_db_name_map_has_no_hardcoded_guid(self):
        """The market map is name->name (resolved to a GUID only via a DB
        query in _build_market_map) -- this pins that the module-level
        constant never embeds a GUID literal."""
        for db_name in harti_loader._PARSER_MARKET_TO_DB_NAME.values():
            assert isinstance(db_name, str)
            # A GUID string would contain hyphens in the 8-4-4-4-12 pattern;
            # market display names should not look like that.
            assert not (len(db_name) == 36 and db_name.count("-") == 4), (
                "DB market name %r looks like a hardcoded GUID" % db_name
            )

    def test_seeded_market_names_match_migration(self):
        """Pin the exact DB Markets.Name strings this loader expects,
        against the seeded rows in AddMultiMarketAndPointInTimeData."""
        assert harti_loader._PARSER_MARKET_TO_DB_NAME["Dambulla"] == "Dambulla Dedicated Economic Centre"
        assert harti_loader._PARSER_MARKET_TO_DB_NAME["Pettah"] == "Pettah (HARTI wholesale)"
        assert harti_loader._PARSER_MARKET_TO_DB_NAME["Narahenpita"] == "Narahenpita (HARTI retail)"

    def test_invalid_price_still_counted_independently_of_market_resolution(self):
        fake_map = self._fake_market_map()
        rows = [
            ParsedPrice("2019-01-01", "Beans", "0-0", 0.0, 0.0, market_name="Dambulla"),
        ]
        original = harti_loader._build_market_map
        harti_loader._build_market_map = lambda _: fake_map
        try:
            result = harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)
        finally:
            harti_loader._build_market_map = original
        assert result["skipped_invalid_price"] == 1
        assert result["inserted"] == 0


# ===========================================================================
# 6. AsOfUtc resolution (point-in-time contract)
# ===========================================================================

class TestAsOfUtc:
    def test_pdf_creation_date_parses_to_correct_utc_instant(self):
        result = harti_loader._resolve_as_of_utc("D:20250202142417+05'30'", date(2025, 2, 1))
        assert result == datetime(2025, 2, 2, 8, 54, 17, tzinfo=timezone.utc)

    def test_creation_date_can_be_after_observed_date(self):
        """Bulletins are typically published the morning AFTER the observed
        date -- AsOfUtc capturing that lag (not collapsing to ObservedDate)
        is the whole point of the point-in-time contract."""
        result = harti_loader._resolve_as_of_utc("D:20150623101458+05'30'", date(2015, 6, 22))
        assert result.date() == date(2015, 6, 23)
        assert result > datetime(2015, 6, 22, 23, 59, 59, tzinfo=timezone.utc)

    def test_missing_creation_date_falls_back_to_conservative_late_vintage(self):
        """Fallback must be ObservedDate+1 06:00 Sri Lanka time (+05:30) =
        ObservedDate+1 00:30 UTC -- conservative-LATE relative to the real
        ~04:30-10:00 SL publication window observed in the corpus, never a
        same-day 23:59:59 UTC value (which would sit ~4-5h EARLIER than
        genuine publication and risk look-ahead leakage)."""
        result = harti_loader._resolve_as_of_utc(None, date(2025, 2, 1))
        assert result == datetime(2025, 2, 2, 0, 30, 0, tzinfo=timezone.utc)

    def test_garbage_creation_date_falls_back_safely(self):
        result = harti_loader._resolve_as_of_utc("not-a-date", date(2025, 2, 1))
        assert result == datetime(2025, 2, 2, 0, 30, 0, tzinfo=timezone.utc)

    def test_fallback_is_never_before_observed_date(self):
        """The leakage guard requires AsOfUtc to never be default(DateTime);
        our fallback must also never be EARLIER than ObservedDate, which
        would make an observation look like it was already known before its
        own economic event date."""
        obs = date(2025, 2, 1)
        result = harti_loader._resolve_as_of_utc(None, obs)
        assert result.date() >= obs

    def test_fallback_is_conservative_late_not_same_day_end_of_day(self):
        """R1-adjacent leakage regression: the fallback must NOT be
        23:59:59 UTC on ObservedDate (the old, leaky value) -- that sits
        hours EARLIER than HARTI's real next-morning publication window and
        would let a point-in-time join see the row too early."""
        obs = date(2025, 2, 1)
        result = harti_loader._resolve_as_of_utc(None, obs)
        leaky_same_day_end_of_day = datetime(2025, 2, 1, 23, 59, 59, tzinfo=timezone.utc)
        assert result > leaky_same_day_end_of_day, (
            "Fallback must be strictly later than the old same-day 23:59:59 UTC value"
        )
        assert result.date() == obs + timedelta(days=1), (
            "Fallback must land on ObservedDate+1, matching real bulletin publication timing"
        )

    def test_utc_offset_with_z_suffix_handled(self):
        result = harti_loader._resolve_as_of_utc("D:20250202142417Z", date(2025, 2, 1))
        assert result == datetime(2025, 2, 2, 14, 24, 17, tzinfo=timezone.utc)

    def test_negative_offset_handled(self):
        # Hypothetical negative-offset input -- must still convert correctly.
        result = harti_loader._resolve_as_of_utc("D:20250202080000-05'00'", date(2025, 2, 1))
        assert result == datetime(2025, 2, 2, 13, 0, 0, tzinfo=timezone.utc)

    def test_offset_without_quote_delimiters_handled(self):
        """R1-adjacent hardening: '+0530' (no apostrophe delimiters) must
        parse identically to '+05\'30\'' -- previously this fell through to
        the 'no offset info present' branch and was silently misread as
        already-UTC (a 5.5h-early look-ahead risk), instead of correctly
        applying the +05:30 offset."""
        result = harti_loader._resolve_as_of_utc("D:20250202142417+0530", date(2025, 2, 1))
        assert result == datetime(2025, 2, 2, 8, 54, 17, tzinfo=timezone.utc)

    def test_offset_without_quote_delimiters_matches_quoted_form(self):
        quoted = harti_loader._resolve_as_of_utc("D:20250202142417+05'30'", date(2025, 2, 1))
        unquoted = harti_loader._resolve_as_of_utc("D:20250202142417+0530", date(2025, 2, 1))
        assert quoted == unquoted

    def test_negative_offset_without_quote_delimiters_handled(self):
        result = harti_loader._resolve_as_of_utc("D:20220616021402-0700", date(2022, 6, 16))
        assert result == datetime(2022, 6, 16, 9, 14, 2, tzinfo=timezone.utc)

    def test_as_of_utc_used_in_upserted_rows(self):
        """End-to-end: upsert_harti_price_observations must set AsOfUtc from
        the parsed row's pdf_creation_date_raw, not datetime.now()."""
        fake_map = {"Dambulla": uuid.UUID("aaaaaaaa-0000-0000-0000-000000000001")}
        rows = [
            ParsedPrice(
                "2019-01-01", "Beans", "100-120", 100.0, 120.0,
                market_name="Dambulla",
                pdf_creation_date_raw="D:20190102101632+05'30'",
            ),
        ]
        original = harti_loader._build_market_map
        harti_loader._build_market_map = lambda _: fake_map
        try:
            # dry_run doesn't execute SQL, so assert via _resolve_as_of_utc
            # directly reflecting what the row would carry.
            expected_as_of = harti_loader._resolve_as_of_utc(
                rows[0].pdf_creation_date_raw, date(2019, 1, 1)
            )
            result = harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)
        finally:
            harti_loader._build_market_map = original

        assert expected_as_of == datetime(2019, 1, 2, 4, 46, 32, tzinfo=timezone.utc)
        assert result["inserted"] == 1


# ===========================================================================
# 7. Dambulla back-compat (upsert_harti_prices unaffected by multi-market)
# ===========================================================================

class TestDambullaBackCompat:
    """upsert_harti_prices() (legacy MarketPrices path) must filter to
    Dambulla-only and otherwise behave exactly as before multi-market rows
    were introduced."""

    def _fake_crop_map(self):
        return {
            "Beans": (uuid.UUID("deadbeef-0000-0000-0000-000000000001"), "Beans"),
        }

    def test_non_dambulla_rows_are_skipped_not_inserted(self):
        fake_map = self._fake_crop_map()
        rows = [
            ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla"),
            ParsedPrice("2019-01-01", "Beans", "80-100", 80.0, 100.0, market_name="Pettah"),
            ParsedPrice("2019-01-01", "Beans", "90-110", 90.0, 110.0, market_name="Narahenpita"),
        ]
        original = harti_loader._build_crop_map
        harti_loader._build_crop_map = lambda _: fake_map
        original_market_id = harti_loader._dambulla_market_id
        harti_loader._dambulla_market_id = lambda _: uuid.UUID("aaaaaaaa-0000-0000-0000-000000000099")
        try:
            result = harti_loader.upsert_harti_prices(rows, engine=MagicMock(), dry_run=True)
        finally:
            harti_loader._build_crop_map = original
            harti_loader._dambulla_market_id = original_market_id

        assert result["inserted"] == 1, "Only the Dambulla row should be inserted"
        assert result["skipped_non_dambulla"] == 2

    def test_dambulla_only_batch_unaffected(self):
        """A pure-Dambulla batch (as in pre-R1.1 P1 callers) behaves
        identically -- zero skipped_non_dambulla."""
        fake_map = self._fake_crop_map()
        rows = [
            ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0, market_name="Dambulla"),
        ]
        original = harti_loader._build_crop_map
        harti_loader._build_crop_map = lambda _: fake_map
        original_market_id = harti_loader._dambulla_market_id
        harti_loader._dambulla_market_id = lambda _: uuid.UUID("aaaaaaaa-0000-0000-0000-000000000099")
        try:
            result = harti_loader.upsert_harti_prices(rows, engine=MagicMock(), dry_run=True)
        finally:
            harti_loader._build_crop_map = original
            harti_loader._dambulla_market_id = original_market_id

        assert result["inserted"] == 1
        assert result["skipped_non_dambulla"] == 0

    def test_default_market_name_is_dambulla(self):
        """A ParsedPrice constructed without market_name (positional 5-arg
        legacy call site) defaults to Dambulla -- old call sites keep working."""
        pr = ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0)
        assert pr.market_name == "Dambulla"
        assert pr.arrivals_kg is None


# ===========================================================================
# 8. Thambuttegama / Keppetipola (R1.1 P2, ClickUp 86cahef44)
# ===========================================================================

class TestThambuttegamaKeppetipola:
    """New-market header aliases for Thambuttegama and Keppetipola, verified
    against real spelling variants observed across the full HARTI corpus
    (2015-06-22 .. 2026-06-27) -- see harti_multimarket_audit.md Sec 8 for
    the underlying per-PDF evidence this pins.

    Both markets are appended to _TARGET_MARKETS (parser.py); Keppetipola's
    column does not exist before 2017-03-08 (7-column era), which is
    exercised here as an ordinary "header not located" WARN-skip case, not
    a special code path -- the detect-don't-hardcode contract handles a
    market's column not existing yet exactly the same way it handles a
    market's column being renamed.
    """

    # -- Real header rows lifted verbatim from the corpus scan (see audit) --
    _HEADER_2015_7COL = [
        None, "Pettah\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "Norochchole\nMarket", "Thambuththegama\nMarket",
    ]
    _HEADER_2017_9COL = [
        None, "Pettah\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "Norochchole\nMarket", "T'thegama\nMarket",
        "Kappetipola\nMarket", "Nuwaraeliya\nMarket",
    ]
    _HEADER_2019_SPLIT = [
        None, "Pettah\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "Norochchole\nMarket", "Thambuththegam\nMarket",
        "a Kappetipola\nMarket", "Nuwaraeliya\nMarket",
    ]
    _HEADER_2022_BLEED = [
        None, "Peliyagoda\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "NorochcholeT\nMarket", "hambuththegam\nMarket",
        "aKappetipola\nMarket", "Nuwaraeliya\nMarket", "Bandarawela\nMarket",
        "Veyangoda\nMarket",
    ]
    _HEADER_2023_BLEED_RESPELLED = [
        None, "Peliyagoda\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "NorochcholeT\nMarket", "hambuththegam\nMarket",
        "aKeppetipola\nMarket", "Nuwaraeliya\nMarket", "Bandarawela\nMarket",
        "Veyangoda\nMarket",
    ]
    _HEADER_2025_CLEAN = [
        None, "Peliyagoda\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "Norochchole\nMarket", "Thambuththegama\nMarket",
        "Keppetipola\nMarket", "Nuwaraeliya\nMarket", "Bandarawela\nMarket",
        "Veyangoda\nMarket",
    ]

    def _table(self, header2):
        header1 = ["Variety"] + ["d1"] * (len(header2) - 1)
        header3 = [""] * len(header2)
        return [header1, header2, header3]

    # -- Thambuttegama spelling variants --

    def test_thambuttegama_located_2015_clean_spelling(self):
        table = self._table(self._HEADER_2015_7COL)
        col = harti_parser._locate_market_column(table, "Thambuttegama")
        assert col == 6

    def test_thambuttegama_located_2017_abbreviated_spelling(self):
        table = self._table(self._HEADER_2017_9COL)
        col = harti_parser._locate_market_column(table, "Thambuttegama")
        assert col == 6
        assert table[1][col] == "T'thegama\nMarket"

    def test_thambuttegama_located_2019_split_artifact(self):
        table = self._table(self._HEADER_2019_SPLIT)
        col = harti_parser._locate_market_column(table, "Thambuttegama")
        assert col == 6
        assert table[1][col] == "Thambuththegam\nMarket"

    def test_thambuttegama_located_2022_bleed_artifact(self):
        """The pdfplumber cell-bleed artifact ('NorochcholeT' / 'hambuththegam'
        / 'aKappetipola') must still resolve Thambuttegama to its own column,
        not the neighbouring Norochchole (non-target) or Keppetipola column."""
        table = self._table(self._HEADER_2022_BLEED)
        col = harti_parser._locate_market_column(table, "Thambuttegama")
        assert col == 6
        assert table[1][col] == "hambuththegam\nMarket"
        # Must NOT collide with the neighbouring Norochchole/Keppetipola cells
        keppetipola_col = harti_parser._locate_market_column(table, "Keppetipola")
        assert col != keppetipola_col

    def test_thambuttegama_located_2025_clean_respelling(self):
        table = self._table(self._HEADER_2025_CLEAN)
        col = harti_parser._locate_market_column(table, "Thambuttegama")
        assert col == 6
        assert table[1][col] == "Thambuththegama\nMarket"

    # -- Keppetipola spelling variants --

    def test_keppetipola_missing_before_2017_returns_none(self):
        """Keppetipola's column does not exist in the original 7-column
        format (confirmed absent 2015-06-22 .. 2017-03-07 in the real
        corpus) -- this is an ordinary missing-header WARN-skip, not a
        special case."""
        table = self._table(self._HEADER_2015_7COL)
        col = harti_parser._locate_market_column(table, "Keppetipola")
        assert col is None

    def test_keppetipola_located_2017_missing_e_spelling(self):
        table = self._table(self._HEADER_2017_9COL)
        col = harti_parser._locate_market_column(table, "Keppetipola")
        assert col == 7
        assert table[1][col] == "Kappetipola\nMarket"

    def test_keppetipola_located_2019_split_artifact(self):
        table = self._table(self._HEADER_2019_SPLIT)
        col = harti_parser._locate_market_column(table, "Keppetipola")
        assert col == 7
        assert table[1][col] == "a Kappetipola\nMarket"

    def test_keppetipola_located_2022_bleed_artifact(self):
        table = self._table(self._HEADER_2022_BLEED)
        col = harti_parser._locate_market_column(table, "Keppetipola")
        assert col == 7
        assert table[1][col] == "aKappetipola\nMarket"

    def test_keppetipola_located_2023_bleed_respelled(self):
        """HARTI corrected the spelling to 'Keppetipola' (extra 'e') while
        the bleed-through leading 'a' artifact persisted -- both aliases
        ('aKappetipola' and 'aKeppetipola') must resolve."""
        table = self._table(self._HEADER_2023_BLEED_RESPELLED)
        col = harti_parser._locate_market_column(table, "Keppetipola")
        assert col == 7
        assert table[1][col] == "aKeppetipola\nMarket"

    def test_keppetipola_located_2025_clean_respelling(self):
        table = self._table(self._HEADER_2025_CLEAN)
        col = harti_parser._locate_market_column(table, "Keppetipola")
        assert col == 7
        assert table[1][col] == "Keppetipola\nMarket"

    # -- Substring-safety (the key R1 risk for these two markets specifically:
    #    their aliases sit adjacent to each other and to Norochchole in the
    #    bleed-artifact era, so accidental cross-matches are the realistic
    #    failure mode here) --

    def test_no_new_alias_is_substring_of_a_different_markets_alias(self):
        """None of the new Thambuttegama/Keppetipola aliases may be a
        substring of any OTHER market's alias (Dambulla, Pettah, Narahenpita,
        or the other new market) -- intra-market substrings (e.g.
        'Kappetipola' inside 'aKappetipola', both Keppetipola) are fine and
        expected; cross-market substrings would be a real R1 risk."""
        aliases = harti_parser._MARKET_HEADER_ALIASES
        for market_a, aliases_a in aliases.items():
            for market_b, aliases_b in aliases.items():
                if market_a == market_b:
                    continue
                for alias_a in aliases_a:
                    for alias_b in aliases_b:
                        assert alias_a not in alias_b, (
                            f"Alias {alias_a!r} ({market_a}) is a substring of "
                            f"{alias_b!r} ({market_b}) -- cross-market collision risk"
                        )

    def test_new_aliases_do_not_match_other_bulletin_headers(self):
        """New aliases must not accidentally match non-target market header
        text seen in the real bulletin (Kandy, Meegoda, Norochchole/
        NorochcholeT, Nuwaraeliya, Bandarawela, Veyangoda)."""
        other_headers = [
            "Kandy\nMarket", "Meegoda\nMarket", "Norochchole\nMarket",
            "NorochcholeT\nMarket", "Nuwaraeliya\nMarket",
            "Bandarawela\nMarket", "Veyangoda\nMarket",
        ]
        for market in ("Thambuttegama", "Keppetipola"):
            for alias in harti_parser._MARKET_HEADER_ALIASES[market]:
                for header in other_headers:
                    assert alias not in header, (
                        f"Alias {alias!r} ({market}) unexpectedly matches "
                        f"non-target header {header!r}"
                    )

    def test_thambuttegama_and_keppetipola_columns_never_collide(self):
        """Across every real header variant fixtured here, the two new
        markets must resolve to two DIFFERENT columns whenever both are
        present."""
        for header in (
            self._HEADER_2017_9COL, self._HEADER_2019_SPLIT,
            self._HEADER_2022_BLEED, self._HEADER_2023_BLEED_RESPELLED,
            self._HEADER_2025_CLEAN,
        ):
            table = self._table(header)
            t_col = harti_parser._locate_market_column(table, "Thambuttegama")
            k_col = harti_parser._locate_market_column(table, "Keppetipola")
            assert t_col is not None and k_col is not None
            assert t_col != k_col

    # -- parse_pdf() integration: missing-column WARN behaviour --

    def _run_parse_pdf_with_header(self, monkeypatch, tmp_path, header2, crop_cells):
        """Build a full synthetic table (header + one crop row) and run it
        through parse_pdf() via the same monkeypatch harness as
        TestParsePdfMultiMarket."""
        header1 = ["Variety"] + ["d1"] * (len(header2) - 1)
        header3 = [""] * len(header2)
        row = ["Beans"] + crop_cells
        table = [header1, header2, header3, row]

        fake_pdf_path = tmp_path / "fake.pdf"
        fake_pdf_path.write_bytes(b"%PDF-1.4 fake")

        class _FakePdf:
            def __init__(self):
                self.pages = [object()]
                self.metadata = {"CreationDate": "D:20190102101632+05'30'"}

            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

        monkeypatch.setattr(harti_parser, "_find_english_veg_page", lambda pdf: (0, table))
        import pdfplumber
        monkeypatch.setattr(pdfplumber, "open", lambda *_a, **_k: _FakePdf())

        return harti_parser.parse_pdf(fake_pdf_path, "2019-01-01")

    def test_keppetipola_missing_before_2017_warn_skips_only_that_market(self, monkeypatch, tmp_path, caplog):
        """2015-format (7-column, no Keppetipola) run through the real
        parse_pdf() entrypoint: Keppetipola WARN-skips, Dambulla/Pettah/
        Thambuttegama still parse normally -- proves the missing-market
        contract for these two new markets matches the existing
        Narahenpita/Dambulla-missing contract exactly, no special-casing."""
        header2 = self._HEADER_2015_7COL
        crop_cells = ["220.00 - 240.00", "190.00- 200.00", "190.00 - 210.00",
                      "230.00 - 240.00", "-", "210.00 - 220.00"]
        rows = self._run_parse_pdf_with_header(monkeypatch, tmp_path, header2, crop_cells)
        markets = {r.market_name for r in rows}
        assert "Keppetipola" not in markets
        assert "Thambuttegama" in markets
        assert "Dambulla" in markets
        assert "Pettah" in markets
        assert any(
            "Keppetipola" in rec.message and "not located" in rec.message
            for rec in caplog.records
        )

    def test_both_new_markets_located_and_parsed_2025_format(self, monkeypatch, tmp_path):
        """Full end-to-end parse against the current (2025-onward) clean
        header format: both new markets emit rows with correct, distinct
        prices."""
        header2 = self._HEADER_2025_CLEAN
        # cells: [Peliyagoda, Kandy, Dambulla, Meegoda, Norochchole,
        #         Thambuththegama, Keppetipola, Nuwaraeliya, Bandarawela, Veyangoda]
        crop_cells = [
            "400- 500", "400- 450", "350- 400", "450- 490", "-",
            "300- 340", "280- 310", "380- 420", "-", "350 - 420",
        ]
        rows = self._run_parse_pdf_with_header(monkeypatch, tmp_path, header2, crop_cells)
        by_market = {r.market_name: (r.min_price, r.max_price) for r in rows}
        assert by_market["Dambulla"] == (350.0, 400.0)
        assert by_market["Thambuttegama"] == (300.0, 340.0)
        assert by_market["Keppetipola"] == (280.0, 310.0)
        # Distinct from each other and from Dambulla -- no cross-contamination
        assert len({by_market["Dambulla"], by_market["Thambuttegama"], by_market["Keppetipola"]}) == 3

    # -- Loader: market-name resolution for the two new markets --

    def test_thambuttegama_and_keppetipola_resolve_via_loader_market_map(self):
        """Mirrors TestMarketNameResolution but for the two new markets --
        proves upsert_harti_price_observations() resolves them by name
        exactly like the R1.1 P1 trio, no special-casing."""
        fake_map = {
            "Thambuttegama": uuid.UUID("b2a20001-0000-0000-0000-000000000003"),
            "Keppetipola": uuid.UUID("b2a20001-0000-0000-0000-000000000002"),
        }
        rows = [
            ParsedPrice("2026-06-24", "Beans", "300-340", 300.0, 340.0, market_name="Thambuttegama"),
            ParsedPrice("2026-06-24", "Beans", "280-310", 280.0, 310.0, market_name="Keppetipola"),
        ]
        original = harti_loader._build_market_map
        harti_loader._build_market_map = lambda _: fake_map
        try:
            result = harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)
        finally:
            harti_loader._build_market_map = original

        assert result["inserted"] == 2
        assert result["skipped_no_market"] == 0

    def test_thambuttegama_unresolved_when_db_row_missing_warn_skip(self, caplog):
        """If the DB Markets row for Thambuttegama isn't seeded in some
        environment, the row must WARN-skip -- never invent a market or
        silently fall back to another market's GUID."""
        fake_map = {
            "Keppetipola": uuid.UUID("b2a20001-0000-0000-0000-000000000002"),
            # Thambuttegama deliberately absent
        }
        rows = [
            ParsedPrice("2026-06-24", "Beans", "300-340", 300.0, 340.0, market_name="Thambuttegama"),
            ParsedPrice("2026-06-24", "Beans", "280-310", 280.0, 310.0, market_name="Keppetipola"),
        ]
        original = harti_loader._build_market_map
        harti_loader._build_market_map = lambda _: fake_map
        try:
            result = harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)
        finally:
            harti_loader._build_market_map = original

        assert result["inserted"] == 1
        assert result["skipped_no_market"] == 1
        assert any(
            "Thambuttegama" in rec.message and "did not resolve" in rec.message
            for rec in caplog.records
        )

    def test_seeded_market_names_for_new_markets_match_migration(self):
        """Pin the exact seeded Markets.Name strings for the new markets,
        same pattern as the R1.1 P1 pin for Dambulla/Pettah/Narahenpita.
        Covers Keppetipola / Thambuttegama (MKT00000002 / MKT00000003) plus
        the 6 R2 Step 6.2 markets (MKT00000007..MKT00000012). These strings
        must stay byte-for-byte identical to SeedMarkets in
        AgriForecastDbContext.cs or _build_market_map WARN-skips at runtime."""
        expected = {
            "Keppetipola":  "Keppetipola Dedicated Economic Centre",
            "Thambuttegama": "Thambuttegama Dedicated Economic Centre",
            # R2 Step 6.2 — 6 new markets, owner-verified classification:
            "Kandy":         "Kandy (HARTI wholesale)",
            "Meegoda":       "Meegoda Dedicated Economic Centre",
            "Norochchole":   "Norochchole (HARTI wholesale)",
            "Nuwara Eliya":  "Nuwara Eliya Dedicated Economic Centre",
            "Bandarawela":   "Bandarawela (HARTI wholesale)",
            "Veyangoda":     "Veyangoda Dedicated Economic Centre",
        }
        for parser_name, db_name in expected.items():
            assert (
                harti_loader._PARSER_MARKET_TO_DB_NAME[parser_name] == db_name
            ), f"{parser_name} DB name drifted from seed"

    def test_target_markets_includes_both_new_markets(self):
        """_TARGET_MARKETS must include both new markets (parse_pdf() only
        extracts markets listed here)."""
        assert "Thambuttegama" in harti_parser._TARGET_MARKETS
        assert "Keppetipola" in harti_parser._TARGET_MARKETS


# ===========================================================================
# 9. R2 Step 6.1 (ClickUp D-DF6) -- the remaining 6 markets: Kandy, Meegoda,
#    Norochchole, Nuwara Eliya, Bandarawela, Veyangoda
# ===========================================================================

class TestSixNewMarketsStep6_1:
    """Full 4 -> 10 market widen. Header evidence sourced from the cached
    corpus (--no-download, zero new scraping) via a stratified sample plus
    binary search for the two markets with a confirmed introduction date
    (Bandarawela, Veyangoda) -- see parser.py's _MARKET_HEADER_ALIASES
    docstring for the full per-market evidence this pins.
    """

    _HEADER_2015_7COL = [
        None, "Pettah\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "Norochchole\nMarket", "Thambuththegama\nMarket",
    ]
    _HEADER_2017_9COL = [
        None, "Pettah\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "Norochchole\nMarket", "T'thegama\nMarket",
        "Kappetipola\nMarket", "Nuwaraeliya\nMarket",
    ]
    # 2021-12-01..09: real cached-corpus era, "ambuththega"/"maKappetipola"
    # cell-bleed variant PRE-DATES the more commonly-known "hambuththegam"/
    # "aKappetipola" era (which starts 2021-12-10) -- Bandarawela not yet
    # introduced (introduced precisely 2021-12-02, so this fixture uses the
    # 2021-12-01 pre-introduction moment specifically, no Bandarawela column).
    _HEADER_2021_12_01_PRE_BANDARAWELA = [
        None, "Petha\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "NorochcholTeh\nMarket", "ambuththega\nMarket",
        "maKappetipola\nMarket", "Nuwaraeliya\nMarket",
    ]
    _HEADER_2021_12_02_WITH_BANDARAWELA = [
        None, "Petha\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "NorochcholTeh\nMarket", "ambuththega\nMarket",
        "maKappetipola\nMarket", "Nuwaraeliya\nMarket", "Bandarawela\nMarket",
    ]
    # 2022-02-21 (10-column, Bandarawela present, Veyangoda NOT yet) vs
    # 2022-02-22 (11-column, Veyangoda introduced) -- binary-searched exactly
    # against the cache.
    _HEADER_2022_02_21_PRE_VEYANGODA = [
        None, "Peliyagoda\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "NorochcholeT\nMarket", "hambuththegam\nMarket",
        "aKappetipola\nMarket", "Nuwaraeliya\nMarket", "Bandarawela\nMarket",
    ]
    _HEADER_2022_02_22_WITH_VEYANGODA = [
        None, "Peliyagoda\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "NorochcholeT\nMarket", "hambuththegam\nMarket",
        "aKappetipola\nMarket", "Nuwaraeliya\nMarket", "Bandarawela\nMarket",
        "Veyangoda\nMarket",
    ]
    _HEADER_2025_CLEAN = [
        None, "Peliyagoda\nMarket", "Kandy\nMarket", "Dambulla\nMarket",
        "Meegoda\nMarket", "Norochchole\nMarket", "Thambuththegama\nMarket",
        "Keppetipola\nMarket", "Nuwaraeliya\nMarket", "Bandarawela\nMarket",
        "Veyangoda\nMarket",
    ]
    # The single malformed 2022-11-26 PDF (pre-existing "no English veg page
    # found" case, unrelated to this widen -- see harti_multimarket_audit.md
    # Sec 4): its raw header row carries truncated/spaced variants that are
    # NOT reachable via the normal _find_english_veg_page() -> table[1] path
    # for THAT specific PDF, but are kept as defensive aliases in case a
    # different, otherwise-parseable PDF repeats the same truncation.
    _HEADER_2022_11_26_MALFORMED_VARIANTS = [
        None, "Peliyagoda", "Norochchole", "Kandy", "Nuwara Eliya",
        "DambullaTh", "ambuththega", "maKappetipola", "Meegoda*",
        "Bandarawela", "Veyangod*",
    ]

    def _table(self, header2):
        header1 = ["Variety"] + ["d1"] * (len(header2) - 1)
        header3 = [""] * len(header2)
        return [header1, header2, header3]

    # -- Kandy: stable spelling corpus-wide, no drift observed --

    def test_kandy_located_2015_clean(self):
        table = self._table(self._HEADER_2015_7COL)
        col = harti_parser._locate_market_column(table, "Kandy")
        assert col == 2

    def test_kandy_located_2025_clean(self):
        table = self._table(self._HEADER_2025_CLEAN)
        col = harti_parser._locate_market_column(table, "Kandy")
        assert col == 2

    # -- Meegoda: clean spelling + truncated/star-suffixed variant --

    def test_meegoda_located_2015_clean(self):
        table = self._table(self._HEADER_2015_7COL)
        col = harti_parser._locate_market_column(table, "Meegoda")
        assert col == 4

    def test_meegoda_located_star_suffixed_variant(self):
        """The 2022-11-26 malformed-header PDF truncates to 'Meegoda*' --
        the plain 'Meegoda' alias already substring-matches this via
        _locate_market_column's `alias in cell_text` check, no separate
        alias entry needed."""
        table = self._table(self._HEADER_2022_11_26_MALFORMED_VARIANTS)
        col = harti_parser._locate_market_column(table, "Meegoda")
        assert col == 8
        assert table[1][col] == "Meegoda*"

    # -- Norochchole: clean + two cell-bleed variants --

    def test_norochchole_located_clean_spelling(self):
        table = self._table(self._HEADER_2025_CLEAN)
        col = harti_parser._locate_market_column(table, "Norochchole")
        assert col == 5
        assert table[1][col] == "Norochchole\nMarket"

    def test_norochchole_located_bleed_variant_norochcholet(self):
        """'Norochchole' (clean) IS a substring of 'NorochcholeT' -- same
        market, safe by construction (only one alias needs to hit)."""
        table = self._table(self._HEADER_2022_02_22_WITH_VEYANGODA)
        col = harti_parser._locate_market_column(table, "Norochchole")
        assert col == 5
        assert table[1][col] == "NorochcholeT\nMarket"

    def test_norochchole_located_bleed_variant_norochcholteh(self):
        """'NorochcholTeh' (2021-12-01..09 era) has the trailing 'e'/'h'
        TRANSPOSED relative to 'NorochcholeT' -- 'Norochchole' is NOT a
        substring of this variant, so it needed its own explicit alias
        entry (this is the key non-obvious finding of the 6.1 discovery:
        without it, this specific 9-PDF window would WARN-skip
        Norochchole entirely)."""
        assert "Norochchole" not in "NorochcholTeh", (
            "sanity check: confirms this variant truly needs its own alias"
        )
        table = self._table(self._HEADER_2021_12_01_PRE_BANDARAWELA)
        col = harti_parser._locate_market_column(table, "Norochchole")
        assert col == 5
        assert table[1][col] == "NorochcholTeh\nMarket"

    # -- Nuwara Eliya: no-space "Nuwaraeliya" (standard) + spaced variant --

    def test_nuwara_eliya_located_standard_no_space_spelling(self):
        table = self._table(self._HEADER_2017_9COL)
        col = harti_parser._locate_market_column(table, "Nuwara Eliya")
        assert col == 8
        assert table[1][col] == "Nuwaraeliya\nMarket"

    def test_nuwara_eliya_located_spaced_variant(self):
        """'Nuwara Eliya' (with space) seen in the malformed 2022-11-26 PDF
        -- NOT a substring of 'Nuwaraeliya' or vice versa (the space makes
        them mutually exclusive strings), so both needed explicit entries."""
        assert "Nuwaraeliya" not in "Nuwara Eliya"
        assert "Nuwara Eliya" not in "Nuwaraeliya"
        table = self._table(self._HEADER_2022_11_26_MALFORMED_VARIANTS)
        col = harti_parser._locate_market_column(table, "Nuwara Eliya")
        assert col == 4
        assert table[1][col] == "Nuwara Eliya"

    # -- Bandarawela: introduction window --

    def test_bandarawela_missing_before_introduction_returns_none(self):
        """Bandarawela's column does not exist before its introduction --
        binary-searched exactly against the cache: absent 2021-12-01,
        present 2021-12-02."""
        table = self._table(self._HEADER_2021_12_01_PRE_BANDARAWELA)
        col = harti_parser._locate_market_column(table, "Bandarawela")
        assert col is None

    def test_bandarawela_located_from_introduction_date(self):
        table = self._table(self._HEADER_2021_12_02_WITH_BANDARAWELA)
        col = harti_parser._locate_market_column(table, "Bandarawela")
        assert col == 9

    def test_bandarawela_located_2025_clean(self):
        table = self._table(self._HEADER_2025_CLEAN)
        col = harti_parser._locate_market_column(table, "Bandarawela")
        assert col == 9

    # -- Veyangoda: introduction window --

    def test_veyangoda_missing_before_introduction_returns_none(self):
        """Binary-searched exactly against the cache: absent 2022-02-21,
        present 2022-02-22 (this corrects harti_multimarket_audit.md's
        informal '~2022' estimate to an exact date)."""
        table = self._table(self._HEADER_2022_02_21_PRE_VEYANGODA)
        col = harti_parser._locate_market_column(table, "Veyangoda")
        assert col is None

    def test_veyangoda_located_from_introduction_date(self):
        table = self._table(self._HEADER_2022_02_22_WITH_VEYANGODA)
        col = harti_parser._locate_market_column(table, "Veyangoda")
        assert col == 10

    def test_veyangoda_located_star_suffixed_variant(self):
        """The 2022-11-26 malformed-header PDF truncates to 'Veyangod*' --
        the 'Veyangod' alias already substring-matches this, no separate
        alias entry needed (same reasoning as Meegoda* above)."""
        table = self._table(self._HEADER_2022_11_26_MALFORMED_VARIANTS)
        col = harti_parser._locate_market_column(table, "Veyangoda")
        assert col == 10
        assert table[1][col] == "Veyangod*"

    # -- Substring-safety: FULL matrix across all 11 wired-up markets
    #    (10 "real bulletin" targets + Narahenpita, kept live per the
    #    detect-don't-hardcode contract even though it is corpus-empty --
    #    see parser.py module docstring) + noise --

    def test_full_10_market_alias_matrix_no_cross_market_substring(self):
        """Permanent regression test (audit Sec 8.3 method, extended to the
        full 11-key _MARKET_HEADER_ALIASES set -- 10 markets with real
        bulletin data plus Narahenpita): no alias for any market may be a
        substring of any OTHER market's alias. Only intra-market substrings
        are allowed (checked implicitly by NOT skipping same-market
        pairs)."""
        aliases = harti_parser._MARKET_HEADER_ALIASES
        assert len(aliases) == 11, "Expected exactly 11 _MARKET_HEADER_ALIASES keys"
        collisions = []
        for market_a, aliases_a in aliases.items():
            for market_b, aliases_b in aliases.items():
                if market_a == market_b:
                    continue
                for alias_a in aliases_a:
                    for alias_b in aliases_b:
                        if alias_a in alias_b:
                            collisions.append((market_a, alias_a, market_b, alias_b))
        assert not collisions, (
            "Cross-market alias substring collisions found: %s" % collisions
        )

    def test_full_matrix_does_not_match_generic_bulletin_noise(self):
        """None of the 10 markets' aliases may accidentally match generic
        bulletin header/prose text that is NOT a market name."""
        noise_strings = [
            "Market", "Variety", "Up Country Vegetable",
            "Low country Vegetable", "Serial", "Item", "Rs./kg",
            "2024-03-05",
        ]
        for market, aliases in harti_parser._MARKET_HEADER_ALIASES.items():
            for alias in aliases:
                for noise in noise_strings:
                    assert alias not in noise and noise not in alias, (
                        f"Alias {alias!r} ({market}) collides with generic "
                        f"bulletin text {noise!r}"
                    )

    # -- 10-market column-order shuffle (extends TestColumnOrderShuffle) --

    def _shuffled_table_ten_markets(self):
        """All 10 target markets, deliberately reordered/interleaved so no
        market sits at a 'suspiciously convenient' fixed offset."""
        header1 = ["Variety"] + ["d1"] * 10
        header2 = [
            None,
            "Veyangoda\nMarket", "Kandy\nMarket", "Bandarawela\nMarket",
            "Dambulla\nMarket", "Nuwaraeliya\nMarket", "Thambuththegama\nMarket",
            "Meegoda\nMarket", "Narahenpita\nMarket", "Norochchole\nMarket",
            "Pettah\nMarket",
        ]
        header3 = [""] * len(header1)
        row = [
            "Beans",
            "10-20",    # Veyangoda
            "30-40",    # Kandy
            "50-60",    # Bandarawela
            "70-80",    # Dambulla
            "90-100",   # Nuwara Eliya
            "110-120",  # Thambuttegama
            "130-140",  # Meegoda
            "150-160",  # Narahenpita
            "170-180",  # Norochchole
            "190-200",  # Pettah
        ]
        # Keppetipola deliberately absent from this fixture (11th market
        # would need its own header cell; 10 of the 11 wired-up market keys
        # -- Dambulla/Pettah/Narahenpita/Thambuttegama/Kandy/Meegoda/
        # Norochchole/Nuwara Eliya/Bandarawela/Veyangoda -- already exercise
        # the full shuffle risk).
        return [header1, header2, header3, row]

    def test_ten_markets_map_to_distinct_columns_regardless_of_order(self):
        table = self._shuffled_table_ten_markets()
        markets = (
            "Dambulla", "Pettah", "Narahenpita", "Thambuttegama", "Kandy",
            "Meegoda", "Norochchole", "Nuwara Eliya", "Bandarawela",
            "Veyangoda",
        )
        cols = {m: harti_parser._locate_market_column(table, m) for m in markets}
        assert all(c is not None for c in cols.values()), cols
        assert len(set(cols.values())) == 10, (
            "All 10 markets must resolve to mutually distinct columns: %s" % cols
        )

    def test_ten_markets_prices_pairwise_distinct_no_cross_contamination(self):
        table = self._shuffled_table_ten_markets()
        markets = (
            "Dambulla", "Pettah", "Narahenpita", "Thambuttegama", "Kandy",
            "Meegoda", "Norochchole", "Nuwara Eliya", "Bandarawela",
            "Veyangoda",
        )
        beans_row = table[3]
        prices = {}
        for market in markets:
            col = harti_parser._locate_market_column(table, market)
            prices[market] = harti_parser._parse_price_cell(beans_row[col])
        assert len(set(prices.values())) == 10, (
            "All 10 markets' Beans prices must be pairwise distinct: %s" % prices
        )

    # -- parse_pdf() end-to-end with the widened crop set --

    def _run_parse_pdf_with_header_and_crops(self, monkeypatch, tmp_path, header2, crop_rows):
        """crop_rows: dict[label, list[cell]] (cells aligned to header2)."""
        header1 = ["Variety"] + ["d1"] * (len(header2) - 1)
        header3 = [""] * len(header2)
        rows = [header1, header2, header3]
        for label, cells in crop_rows.items():
            rows.append([label] + cells)
        table = rows

        fake_pdf_path = tmp_path / "fake.pdf"
        fake_pdf_path.write_bytes(b"%PDF-1.4 fake")

        class _FakePdf:
            def __init__(self):
                self.pages = [object()]
                self.metadata = {"CreationDate": "D:20260624141348+05'30'"}

            def __enter__(self):
                return self

            def __exit__(self, *a):
                return False

        monkeypatch.setattr(harti_parser, "_find_english_veg_page", lambda pdf: (0, table))
        import pdfplumber
        monkeypatch.setattr(pdfplumber, "open", lambda *_a, **_k: _FakePdf())

        return harti_parser.parse_pdf(fake_pdf_path, "2026-06-24")

    def test_end_to_end_widened_crop_set_all_10_markets(self, monkeypatch, tmp_path):
        """Synthetic 2025-clean-format table (11 columns incl. Keppetipola)
        with several of the newly-widened crops populated across multiple
        markets -- proves the widened _TARGET_CROPS and the full
        _TARGET_MARKETS set compose correctly end-to-end."""
        header2 = self._HEADER_2025_CLEAN
        crop_rows = {
            "Beans":     ["400- 500", "400- 450", "350- 400", "450- 490", "-", "300- 340", "280- 310", "380- 420", "-", "350- 420"],
            "Tomato":    ["70- 90", "-", "70- 80", "90- 100", "-", "50- 80", "-", "-", "-", "-"],
            "Brinjals":  ["90- 100", "-", "95- 100", "-", "-", "-", "60- 80", "-", "-", "-"],
            "Green Chillies": ["300- 350", "-", "300- 350", "350- 400", "280- 320", "300- 350", "-", "-", "-", "-"],
        }
        rows = self._run_parse_pdf_with_header_and_crops(monkeypatch, tmp_path, header2, crop_rows)
        labels = {r.harti_label for r in rows}
        assert {"Beans", "Tomato", "Brinjals", "Green Chillies"} <= labels
        dambulla_tomato = next(r for r in rows if r.harti_label == "Tomato" and r.market_name == "Dambulla")
        assert (dambulla_tomato.min_price, dambulla_tomato.max_price) == (70.0, 80.0)
        thambuttegama_beans = next(r for r in rows if r.harti_label == "Beans" and r.market_name == "Thambuttegama")
        assert (thambuttegama_beans.min_price, thambuttegama_beans.max_price) == (300.0, 340.0)

    def test_bandarawela_veyangoda_absent_pre_introduction_warn_skip_via_parse_pdf(
        self, monkeypatch, tmp_path, caplog
    ):
        """End-to-end (real parse_pdf() entrypoint): a pre-introduction-era
        header (2021-12-01, Bandarawela/Veyangoda both absent) WARN-skips
        both, other located markets still parse."""
        header2 = self._HEADER_2021_12_01_PRE_BANDARAWELA
        crop_cells = ["80- 100", "-", "100- 120", "160- 165", "-", "90- 120", "-", "-"]
        rows = self._run_parse_pdf_with_header_and_crops(
            monkeypatch, tmp_path, header2, {"Beans": crop_cells}
        )
        markets = {r.market_name for r in rows}
        assert "Bandarawela" not in markets
        assert "Veyangoda" not in markets
        assert "Dambulla" in markets
        assert any("Bandarawela" in rec.message and "not located" in rec.message for rec in caplog.records)
        assert any("Veyangoda" in rec.message and "not located" in rec.message for rec in caplog.records)

    # -- Loader: market-name resolution for the 6 new markets. Post-6.2 the
    #    live Markets rows exist (MKT00000007..MKT00000012); the fail-closed
    #    WARN-skip path below is still exercised via a mock empty map so the
    #    safety net stays covered if a name ever drifts. --

    def test_six_new_markets_all_in_parser_market_to_db_name(self):
        for market in (
            "Kandy", "Meegoda", "Norochchole", "Nuwara Eliya", "Bandarawela",
            "Veyangoda",
        ):
            assert market in harti_loader._PARSER_MARKET_TO_DB_NAME, (
                f"{market} missing from _PARSER_MARKET_TO_DB_NAME"
            )

    def test_six_new_markets_warn_skip_when_db_row_absent(self, caplog):
        """If a new market's Markets row is ever missing (e.g. a name drift
        that breaks the _build_market_map JOIN), upsert_harti_price_
        observations() must WARN-skip its rows, never invent a market.
        Simulated via an empty fake map (mirrors
        test_thambuttegama_unresolved_when_db_row_missing_warn_skip). Post-6.2
        the real DB rows DO exist, so this exercises the safety net, not the
        default path."""
        fake_map = {}  # force the unresolved branch regardless of live state
        rows = [
            ParsedPrice("2026-06-24", "Beans", "30-40", 30.0, 40.0, market_name="Kandy"),
            ParsedPrice("2026-06-24", "Beans", "130-140", 130.0, 140.0, market_name="Meegoda"),
        ]
        original = harti_loader._build_market_map
        harti_loader._build_market_map = lambda _: fake_map
        try:
            result = harti_loader.upsert_harti_price_observations(rows, engine=MagicMock(), dry_run=True)
        finally:
            harti_loader._build_market_map = original

        assert result["skipped_no_market"] == 2
        assert result["inserted"] == 0
        assert any("Kandy" in rec.message and "did not resolve" in rec.message for rec in caplog.records)
        assert any("Meegoda" in rec.message and "did not resolve" in rec.message for rec in caplog.records)

    def test_target_markets_includes_all_ten(self):
        expected = {
            "Dambulla", "Pettah", "Narahenpita", "Thambuttegama", "Keppetipola",
            "Kandy", "Meegoda", "Norochchole", "Nuwara Eliya", "Bandarawela",
            "Veyangoda",
        }
        assert set(harti_parser._TARGET_MARKETS) == expected
        assert len(harti_parser._TARGET_MARKETS) == 11, (
            "10 named markets + note: Narahenpita is wired but corpus-empty "
            "(kept live per the detect-don't-hardcode contract)"
        )
