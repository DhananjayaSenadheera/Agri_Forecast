"""CBSL Daily Price Report pipeline (capture-only v1, feat/cbsl-price-parser).

Parser tests run against two REAL committed report PDFs (corpus-probed
2026-07-22): a Tuesday report (comparison column labelled "Yesterday") and a
Monday report from 2024 (labelled "Last Friday" + a date) — the two known
header variants, two years apart, locking layout-stability assumptions.

Loader tests are hermetic (mocked engine / resolver — no DB).
"""
from __future__ import annotations

from datetime import date
from pathlib import Path
from unittest.mock import MagicMock

import pytest

from agriforecast_ml.cbsl_report import downloader, loader, parser

FIXTURES = Path(__file__).parent / "fixtures" / "cbsl"
TUESDAY_PDF = FIXTURES / "price_report_20260721_e.pdf"
MONDAY_PDF = FIXTURES / "price_report_20240722_e.pdf"


# 1. Number-token merging (the kerning artifact)

def _w(text: str, x0: float, x1: float) -> parser._Word:
    return parser._Word(text=text, x0=x0, x1=x1, top=100.0)


class TestMergeValues:
    def test_kerning_split_digits_merge(self):
        # "2 50.00" -> 250.00 ; "1 ,200.00" -> 1200.00
        vals = parser._merge_values([
            _w("2", 10, 14), _w("50.00", 16, 40),
            _w("1", 60, 64), _w(",200.00", 66, 100),
        ])
        assert [v for v, _x in vals] == ["250.00", "1200.00"]

    def test_na_token_passes_through(self):
        vals = parser._merge_values([_w("n.a.", 10, 30), _w("4", 50, 54), _w("00.00", 56, 80)])
        assert [v for v, _x in vals] == ["n.a.", "400.00"]

    def test_incomplete_run_dropped_never_guessed(self):
        # A lone "2" with no completing decimals must NOT become a price.
        vals = parser._merge_values([_w("2", 10, 14)])
        assert vals == []

    def test_x_centre_spans_the_merged_run(self):
        vals = parser._merge_values([_w("2", 10, 14), _w("50.00", 16, 40)])
        assert vals[0][1] == pytest.approx((10 + 40) / 2)


# 2. Parser against the two REAL header variants

@pytest.fixture(scope="module")
def tuesday_rows():
    return parser.parse_pdf(TUESDAY_PDF)


@pytest.fixture(scope="module")
def monday_rows():
    return parser.parse_pdf(MONDAY_PDF)


class TestParserRealPdfs:
    def test_tuesday_report_parses_today_rows(self, tuesday_rows):
        assert len(tuesday_rows) > 80  # ~99 in the probed report
        assert all(r.date_str == "2026-07-21" for r in tuesday_rows)

    def test_monday_last_friday_variant_parses(self, monday_rows):
        # 2024 Monday report: comparison column is "Last Friday" + a date —
        # the parser must still find 5 comparison anchors and keep TODAY only.
        assert len(monday_rows) > 80
        assert all(r.date_str == "2024-07-22" for r in monday_rows)

    def test_all_five_market_columns_present(self, tuesday_rows):
        markets = {r.market_name for r in tuesday_rows}
        assert markets == set(parser.MARKET_KEYS)

    def test_beans_values_match_the_printed_report(self, tuesday_rows):
        # Ground truth read off the PDF by eye during the corpus probe:
        # wholesale 250 (Pettah) / 350 (Dambulla); retail 300 / 380 / 480.
        beans = {r.market_name: r.price for r in tuesday_rows if r.cbsl_label == "Beans"}
        assert beans == {
            "Pettah (wholesale)": 250.0,
            "Dambulla (wholesale)": 350.0,
            "Pettah (retail)": 300.0,
            "Dambulla (retail)": 380.0,
            "Narahenpita (retail)": 480.0,
        }

    def test_thousands_prices_survive_the_kerning_merge(self, tuesday_rows):
        # Green Chilli trades over Rs 1,000 — the "1 ,200.00" form must land
        # as 1200.0, never 1.0 or 200.0.
        gc = [r.price for r in tuesday_rows if r.cbsl_label == "Green Chilli"]
        assert gc and all(p > 1000 for p in gc)

    def test_out_of_scope_units_and_sections_are_excluded(self, tuesday_rows):
        labels = {r.cbsl_label for r in tuesday_rows}
        # Rs./Each and Rs./Nut and Rs./Ltr rows:
        assert "Egg (White)" not in labels
        assert "Apple (Imp)" not in labels
        assert "Coconut (Avg.)" not in labels
        assert "Coconut oil" not in labels
        # RICE (own sub-market header) and FISH (different markets) sections:
        assert "Samba" not in labels
        assert "Kelawalla" not in labels
        # In-scope OTHER-section kg items ARE captured:
        assert "Red Onion (Local)" in labels

    def test_unit_is_always_the_verified_kg_constant(self, tuesday_rows):
        assert {r.unit_raw for r in tuesday_rows} == {"Rs./kg"}

    def test_pdf_creation_date_metadata_is_carried(self, tuesday_rows):
        # AsOfUtc vintage source — must ride along on every row.
        assert all(r.pdf_creation_date_raw for r in tuesday_rows)


# 3. Downloader date candidates (capture-only contract)

class TestCandidateDates:
    def test_empty_watermark_defaults_to_last_seven_days(self):
        until = date(2026, 7, 22)
        dates = downloader.candidate_dates(None, until)
        assert dates[0] == date(2026, 7, 16)
        assert dates[-1] == until
        assert len(dates) == 7

    def test_since_is_exclusive_lower_bound(self):
        dates = downloader.candidate_dates(date(2026, 7, 20), date(2026, 7, 22))
        assert dates == [date(2026, 7, 21), date(2026, 7, 22)]

    def test_up_to_date_watermark_yields_nothing(self):
        assert downloader.candidate_dates(date(2026, 7, 22), date(2026, 7, 22)) == []

    def test_cap_keeps_the_oldest_dates_so_the_watermark_advances_in_order(self):
        dates = [date(2026, 1, 1), date(2026, 1, 2), date(2026, 1, 3)]
        session = MagicMock()
        session.get.side_effect = AssertionError("network must not be touched")
        # Cap of 0 with a non-empty list: nothing fetched, nothing crashes.
        out = downloader.download_pdfs(Path("/nonexistent"), dates, session=session, max_dates=0)
        assert out == []


# 4. Loader (hermetic): column split + placeholder-market skip

def _row(market_name: str, label: str = "Beans", price: float = 250.0) -> parser.ParsedCbslPrice:
    return parser.ParsedCbslPrice(
        date_str="2026-07-21", cbsl_label=label, unit_raw="Rs./kg",
        market_name=market_name, price=price, pdf_creation_date_raw=None,
    )


class _FakeResolver:
    def __init__(self, engine):
        pass

    def resolve(self, label, source=None):
        return None  # unresolved -> CropId NULL path (capture-only reality day 1)


@pytest.fixture
def hermetic_loader(monkeypatch):
    """Market map resolves the three SEEDED columns only (the two retail
    placeholders are absent, as live); alias resolver never resolves."""
    import uuid
    market_map = {
        "Pettah (wholesale)": uuid.UUID("22222222-2222-2222-2222-222222222222"),
        "Dambulla (wholesale)": uuid.UUID("11111111-1111-1111-1111-111111111111"),
        "Narahenpita (retail)": uuid.UUID("33333333-3333-3333-3333-333333333333"),
    }
    monkeypatch.setattr(loader, "_build_market_map", lambda engine: market_map)
    monkeypatch.setattr(loader, "CommodityAliasResolver", _FakeResolver)
    return market_map


class TestLoaderHermetic:
    def test_placeholder_retail_columns_warn_skip(self, hermetic_loader):
        rows = [_row(m) for m in parser.MARKET_KEYS]  # one per column
        counters = loader.upsert_cbsl_price_observations(
            rows, engine=MagicMock(), dry_run=True)
        assert counters["skipped_no_market"] == 2  # Pettah/Dambulla (retail)
        assert counters["inserted"] == 3           # dry-run "as-if" count

    def test_non_positive_price_rejected(self, hermetic_loader):
        counters = loader.upsert_cbsl_price_observations(
            [_row("Pettah (wholesale)", price=0.0)], engine=MagicMock(), dry_run=True)
        assert counters["skipped_invalid_price"] == 1
        assert counters["inserted"] == 0

    def test_wholesale_and_retail_land_in_their_own_columns(self, hermetic_loader):
        # Non-dry-run against a mocked engine: existing-keys SELECT returns
        # nothing, so every row takes the INSERT branch; assert the point
        # figure lands in WholesalePrice for wholesale columns and RetailPrice
        # for retail ones, with Min/Max always NULL (inverse of HARTI).
        engine = MagicMock()
        conn = MagicMock()
        conn.execute.return_value.fetchall.return_value = []
        engine.begin.return_value.__enter__.return_value = conn

        rows = [_row("Dambulla (wholesale)", price=350.0),
                _row("Narahenpita (retail)", price=480.0)]
        counters = loader.upsert_cbsl_price_observations(rows, engine=engine)
        assert counters["inserted"] == 2

        inserts = [c for c in conn.execute.call_args_list
                   if "INSERT INTO PriceObservations" in str(c[0][0])]
        assert len(inserts) == 2
        by_price = {c[0][1]["wholesale_price"] or c[0][1]["retail_price"]: c[0][1]
                    for c in inserts}
        assert by_price[350.0]["wholesale_price"] == 350.0
        assert by_price[350.0]["retail_price"] is None
        assert by_price[480.0]["retail_price"] == 480.0
        assert by_price[480.0]["wholesale_price"] is None
        for params in by_price.values():
            assert params["source"] == "CBSL"
            assert "min_price" not in params  # Min/Max are literal NULLs in SQL

    def test_insert_sql_writes_null_min_max(self, hermetic_loader):
        engine = MagicMock()
        conn = MagicMock()
        conn.execute.return_value.fetchall.return_value = []
        engine.begin.return_value.__enter__.return_value = conn
        loader.upsert_cbsl_price_observations([_row("Dambulla (wholesale)")], engine=engine)
        insert_sql = next(str(c[0][0]) for c in conn.execute.call_args_list
                          if "INSERT INTO PriceObservations" in str(c[0][0]))
        assert "MinPrice, MaxPrice" in insert_sql
        assert "NULL, NULL" in insert_sql


# 5. Service pass (no-op path)

class TestServiceNoOp:
    def test_empty_cache_no_download_is_a_noop_success(self, tmp_path):
        import ingest_cbsl_service
        summary = ingest_cbsl_service.run(no_download=True, cache_dir=tmp_path)
        assert summary["status"] == "ok"
        assert summary["priceObservations"]["inserted"] == 0
        assert summary["priceObservations"]["maxObservedDate"] is None
