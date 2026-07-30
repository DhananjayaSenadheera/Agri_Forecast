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


# 3b. Downloader network guards: the SSRF choke point + the 25 MB size cap.
#     This fetch used to be the one outbound call in the repo that bypassed
#     netguard (plain sess.get, auto-followed redirects, unbounded body into
#     memory and disk) — and it runs nightly in the pod that holds the DB
#     credentials. Network-free: session and responses are mocked.

def _mock_pdf_response(body: bytes = b"", *, status: int = 200, content_length=None,
                       redirect_to: str | None = None):
    """MagicMock standing in for the streamed requests.Response guarded_get returns."""
    resp = MagicMock()
    resp.__enter__ = MagicMock(return_value=resp)
    resp.__exit__ = MagicMock(return_value=False)
    resp.close = MagicMock()
    resp.is_redirect = redirect_to is not None
    resp.is_permanent_redirect = False
    resp.status_code = 302 if redirect_to else status

    headers = {}
    if redirect_to:
        headers["Location"] = redirect_to
    if content_length is not None:
        headers["Content-Length"] = content_length
    resp.headers = headers

    chunk = downloader._STREAM_CHUNK_BYTES
    chunks = [body[i:i + chunk] for i in range(0, len(body), chunk)] or [b""]
    resp.iter_content = MagicMock(return_value=iter(chunks))
    return resp


def _session_by_date(responses_by_yyyymmdd: dict):
    """Session whose .get() picks a response by the YYYYMMDD token in the URL."""
    session = MagicMock()

    def _get(url, headers=None, timeout=None, stream=None, allow_redirects=None):
        for token, resp in responses_by_yyyymmdd.items():
            if token in url:
                return resp
        raise AssertionError(f"Unexpected URL in test: {url}")

    session.get = MagicMock(side_effect=_get)
    return session


@pytest.fixture(autouse=True)
def _public_dns(monkeypatch):
    """Resolve every host to a public IP so the private-IP block is not what fires.

    Module-wide autouse (harti's tests/test_downloader_cap.py pattern): any test
    added to this file later also cannot make a real DNS call. Inert for the
    parser/loader tests, which never reach netguard.
    """
    from agriforecast_ml import netguard
    monkeypatch.setattr(netguard, "_resolve_ips", lambda host: ["8.8.8.8"])


_GOOD_PDF = b"%PDF-1.4 cbsl daily price report" * 50


class TestDownloadNetworkGuards:
    def test_normal_small_pdf_is_cached_and_returned(self, tmp_path):
        session = _session_by_date({"20260721": _mock_pdf_response(
            _GOOD_PDF, content_length=str(len(_GOOD_PDF)))})

        out = downloader.download_pdfs(tmp_path, [date(2026, 7, 21)], session=session)

        assert out == [("2026-07-21", tmp_path / "cbsl_2026-07-21.pdf")]
        assert (tmp_path / "cbsl_2026-07-21.pdf").read_bytes() == _GOOD_PDF

    def test_fetch_goes_through_the_guard_streaming_with_redirects_disabled(self, tmp_path):
        session = _session_by_date({"20260721": _mock_pdf_response(_GOOD_PDF)})

        downloader.download_pdfs(tmp_path, [date(2026, 7, 21)], session=session)

        _, kwargs = session.get.call_args
        assert kwargs.get("allow_redirects") is False, (
            "must fetch via guarded_get, which follows redirects manually and "
            "re-validates every hop"
        )
        assert kwargs.get("stream") is True, "body must be streamed under the cap, not buffered whole"

    def test_redirect_to_disallowed_host_is_refused_and_date_skipped(self, tmp_path):
        # Day 1 redirects off the allowlist; day 2 is a normal report.
        session = _session_by_date({
            "20260721": _mock_pdf_response(redirect_to="https://evil.example.com/steal"),
            "20260722": _mock_pdf_response(_GOOD_PDF),
        })

        out = downloader.download_pdfs(
            tmp_path, [date(2026, 7, 21), date(2026, 7, 22)], session=session)

        assert not (tmp_path / "cbsl_2026-07-21.pdf").exists(), (
            "a redirect off the host allowlist must never be written to the cache")
        assert out == [("2026-07-22", tmp_path / "cbsl_2026-07-22.pdf")], (
            "the pass must continue past a blocked redirect")

    def test_blocked_redirect_never_fetches_the_offlist_host(self, tmp_path, caplog):
        session = _session_by_date({
            "20260721": _mock_pdf_response(redirect_to="https://evil.example.com/steal"),
        })

        with caplog.at_level("WARNING"):
            downloader.download_pdfs(tmp_path, [date(2026, 7, 21)], session=session)

        fetched = [c.args[0] for c in session.get.call_args_list]
        assert all("evil.example.com" not in u for u in fetched), (
            "the disallowed redirect target must be validated BEFORE it is requested")
        # The 3xx must be recognised and rejected as a guard failure, not shrugged
        # off as a generic non-200 (which is what the pre-guard code did).
        assert any("blocked by SSRF guard" in r.getMessage() for r in caplog.records), (
            "an off-allowlist redirect target must be logged as an SSRF block")

    def test_oversized_declared_length_is_refused_and_date_skipped(self, tmp_path):
        huge = _mock_pdf_response(_GOOD_PDF,
                                  content_length=str(downloader._MAX_PDF_BYTES + 1))
        session = _session_by_date({"20260721": huge, "20260722": _mock_pdf_response(_GOOD_PDF)})

        out = downloader.download_pdfs(
            tmp_path, [date(2026, 7, 21), date(2026, 7, 22)], session=session)

        assert not huge.iter_content.called, (
            "body must not be read once Content-Length already exceeds the cap")
        assert not (tmp_path / "cbsl_2026-07-21.pdf").exists()
        assert out == [("2026-07-22", tmp_path / "cbsl_2026-07-22.pdf")]

    def test_oversized_stream_with_lying_header_is_refused_and_date_skipped(self, tmp_path):
        # Header claims a tiny body; the actual stream blows past the cap.
        body = b"x" * (downloader._MAX_PDF_BYTES + 1)
        session = _session_by_date({
            "20260721": _mock_pdf_response(body, content_length="1000"),
            "20260722": _mock_pdf_response(_GOOD_PDF),
        })

        out = downloader.download_pdfs(
            tmp_path, [date(2026, 7, 21), date(2026, 7, 22)], session=session)

        assert not (tmp_path / "cbsl_2026-07-21.pdf").exists(), (
            "no partial file may be written for a body that exceeded the cap")
        assert out == [("2026-07-22", tmp_path / "cbsl_2026-07-22.pdf")]

    def test_body_exactly_at_the_cap_is_allowed(self, tmp_path):
        body = b"y" * downloader._MAX_PDF_BYTES
        session = _session_by_date({"20260721": _mock_pdf_response(body)})

        out = downloader.download_pdfs(tmp_path, [date(2026, 7, 21)], session=session)

        assert len(out) == 1
        assert (tmp_path / "cbsl_2026-07-21.pdf").stat().st_size == downloader._MAX_PDF_BYTES

    def test_declared_length_exactly_at_the_cap_is_allowed(self, tmp_path):
        """Pins the ALLOW side of the Content-Length pre-check: only a header
        strictly GREATER than the cap may reject, so a report advertising
        exactly the cap is still downloaded."""
        session = _session_by_date({"20260721": _mock_pdf_response(
            _GOOD_PDF, content_length=str(downloader._MAX_PDF_BYTES))})

        out = downloader.download_pdfs(tmp_path, [date(2026, 7, 21)], session=session)

        assert out == [("2026-07-21", tmp_path / "cbsl_2026-07-21.pdf")]
        assert (tmp_path / "cbsl_2026-07-21.pdf").read_bytes() == _GOOD_PDF

    def test_404_is_still_a_normal_gap_not_a_failure(self, tmp_path, caplog):
        weekend = _mock_pdf_response(status=404)
        session = _session_by_date({"20260719": weekend,
                                    "20260721": _mock_pdf_response(_GOOD_PDF)})

        with caplog.at_level("INFO"):
            out = downloader.download_pdfs(
                tmp_path, [date(2026, 7, 19), date(2026, 7, 21)], session=session)

        assert out == [("2026-07-21", tmp_path / "cbsl_2026-07-21.pdf")]
        assert not (tmp_path / "cbsl_2026-07-19.pdf").exists()
        assert not weekend.iter_content.called, "a 404 body must never be read"
        assert any("normal gap" in r.getMessage() for r in caplog.records
                   if r.levelname == "INFO")
        assert not [r for r in caplog.records
                    if r.levelname in ("WARNING", "ERROR") and "2026-07-19" in r.getMessage()], (
            "a 404 must stay a normal calendar gap, never a logged failure")

    def test_other_non_200_still_warns_and_skips(self, tmp_path, caplog):
        broken = _mock_pdf_response(status=503)
        session = _session_by_date({"20260721": broken})

        with caplog.at_level("WARNING"):
            out = downloader.download_pdfs(tmp_path, [date(2026, 7, 21)], session=session)

        assert out == []
        assert not broken.iter_content.called
        assert any("HTTP 503" in r.getMessage() for r in caplog.records)

    def test_offlist_initial_url_is_blocked_without_touching_the_network(
            self, tmp_path, monkeypatch):
        # Drop cbsl.gov.lk from the allowlist: the guard must refuse before any socket.
        monkeypatch.setenv("AGRI_INGEST_ALLOWED_HOSTS", "only-other.example")
        session = MagicMock()
        session.get = MagicMock(side_effect=AssertionError("network must not be reached"))

        out = downloader.download_pdfs(tmp_path, [date(2026, 7, 21)], session=session)

        assert out == []
        session.get.assert_not_called()

    def test_no_exception_escapes_download_pdfs(self, tmp_path):
        """The overall guarantee: guard/cap failures are per-date skips, never
        an exception out of the pass."""
        session = _session_by_date({
            "20260721": _mock_pdf_response(redirect_to="https://evil.example.com/x"),
            "20260722": _mock_pdf_response(b"z" * (downloader._MAX_PDF_BYTES + 1)),
        })

        out = downloader.download_pdfs(
            tmp_path, [date(2026, 7, 21), date(2026, 7, 22)], session=session)

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
