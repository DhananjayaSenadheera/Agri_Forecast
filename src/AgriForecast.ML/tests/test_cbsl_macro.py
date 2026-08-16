"""Tests for the CBSL macro ingestion pipeline (ClickUp 86cahefbh, P3).

Coverage:
  - Parser tests against real fixture PDFs (exact expected values).
  - URL/date-pattern tests (CCPI filename date, MEI filename month, filename
    variants seen in the wild: "_0" Drupal-collision suffix, "er" suffix).
  - Cache-filename sanitization (parsed-date derived, never URL basename).
  - Page-count-cap test.
  - PublishedAt cross-check (CCPI: URL vs /CreationDate, later-wins) +
    conservative-late rule (MEI: no /CreationDate -> lag-prior imputation).
  - SHA-256 recorded per download.
  - netguard: cbsl.gov.lk allowed, statistics.gov.lk NOT allowed.
  - /admin/ingest-cbsl-macro route registered on admin_router + auth-gated
    (mirrors test_admin_auth.py's pattern for /admin/ingest-harti).

Fixtures live in tests/fixtures/cbsl/ (5 real CCPI press releases + 1 real
MEI pack). The original 2 (May/June 2026) are from the step-0 probe; 3 more
(Jul 2025, Dec 2025, Feb 2026) were added for reviewer finding F1 (2026-07-04)
regression coverage of the CCPI_FOOD_YOY month/year-on-different-lines fix —
see cbsl_macro/parser.py's docstring for both.
"""
from __future__ import annotations

import sys
from datetime import date, datetime, timezone
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

FIXTURES_DIR = Path(__file__).parent / "fixtures" / "cbsl"

from agriforecast_ml.cbsl_macro import downloader, loader, parser  # noqa: E402
from agriforecast_ml import netguard  # noqa: E402
import ingest_cbsl_macro  # noqa: E402


# 1. Parser tests against real fixture PDFs — exact expected values

class TestCcpiParserFixtures:
    """Values below were independently read off the real fixture text
    (pdfplumber page.extract_text()) during the step-0 probe — see the
    WRITE-BACK / DECISIONS.md entry for the raw extraction."""

    def test_may_2026_release_index_and_yoy(self):
        pdf = FIXTURES_DIR / "press_20260529_inflation_in_may_2026_ccpi_e.pdf"
        points = parser.parse_ccpi_pdf(pdf, filename_pub_date=date(2026, 5, 29))
        by_series = {p.series_code: p for p in points}

        assert by_series[parser.SERIES_CCPI_INDEX].value == pytest.approx(203.4)
        assert by_series[parser.SERIES_CCPI_HEADLINE_YOY].value == pytest.approx(5.5)
        assert by_series[parser.SERIES_CCPI_FOOD_YOY].value == pytest.approx(0.9)
        for p in points:
            assert p.reference_date == date(2026, 5, 1)
            assert p.source == "CBSL_CCPI"
            assert p.extra["base_year"] == "2021"

    def test_june_2026_release_index_and_yoy(self):
        pdf = FIXTURES_DIR / "press_20260630_inflation_in_june_2026_ccpi_e.pdf"
        points = parser.parse_ccpi_pdf(pdf, filename_pub_date=date(2026, 6, 30))
        by_series = {p.series_code: p for p in points}

        assert by_series[parser.SERIES_CCPI_INDEX].value == pytest.approx(207.7)
        assert by_series[parser.SERIES_CCPI_HEADLINE_YOY].value == pytest.approx(6.8)
        assert by_series[parser.SERIES_CCPI_FOOD_YOY].value == pytest.approx(3.6)
        for p in points:
            assert p.reference_date == date(2026, 6, 1)

    def test_food_yoy_never_matches_non_food_line(self):
        """Regression test for the real bug found during development: a naive
        'Food inflation (Y-o-Y)' regex matches the substring inside 'Non-Food
        inflation (Y-o-Y)', which appears FIRST in the May release and carries
        a completely different number (7.8, Non-Food) than the true Food
        figure (0.9). This must never regress."""
        pdf = FIXTURES_DIR / "press_20260529_inflation_in_may_2026_ccpi_e.pdf"
        points = parser.parse_ccpi_pdf(pdf, filename_pub_date=date(2026, 5, 29))
        food = next(p for p in points if p.series_code == parser.SERIES_CCPI_FOOD_YOY)
        assert food.value == pytest.approx(0.9), (
            "Food YoY must be 0.9 (the true Food figure), not 7.8 (Non-Food) — "
            "a naive substring match on 'Food inflation (Y-o-Y)' would wrongly "
            "capture the Non-Food sentence, which appears earlier in the text."
        )

    def test_pdf_creation_date_recovered(self):
        pdf = FIXTURES_DIR / "press_20260630_inflation_in_june_2026_ccpi_e.pdf"
        points = parser.parse_ccpi_pdf(pdf, filename_pub_date=date(2026, 6, 30))
        assert all(p.pdf_creation_date_raw for p in points), (
            "Real CCPI fixtures carry a /CreationDate per the step-0 probe"
        )
        assert points[0].pdf_creation_date_raw.startswith("D:20260630")


class TestCcpiFoodYoyRecallFix:
    """Regression tests for reviewer finding F1 (2026-07-04): the food-YoY
    value regex was coupled to a contiguous trailing '<Month> <Year>',
    which silently failed on releases where pdfplumber's linear extraction
    interleaves an inset chart's caption BETWEEN the month and the year.
    ReferenceDate for the food series must always come from the shared
    column-header/filename resolution, never from text after the food
    sentence itself.
    """

    def test_july_2025_month_and_year_on_different_lines(self):
        """cbsl_ccpi_20250731.pdf verbatim:
          'Food inflation (Y-o-Y) moderated further to 1.5% in July\\n
           % CCPI (2021=100)\\n2025 from 4.3% recorded in June 2025, ...'
        Month ('July') and year ('2025') are on DIFFERENT lines, separated
        by an inset-chart caption. Must still recover 1.5% at ReferenceDate
        2025-07-01 (not fail, and not read a wrong year)."""
        pdf = FIXTURES_DIR / "cbsl_ccpi_20250731.pdf"
        points = parser.parse_ccpi_pdf(pdf, filename_pub_date=date(2025, 7, 31))
        food = next((p for p in points if p.series_code == parser.SERIES_CCPI_FOOD_YOY), None)
        assert food is not None, "Food YoY must be recovered despite the month/year line split"
        assert food.value == pytest.approx(1.5)
        assert food.reference_date == date(2025, 7, 1)

    def test_remained_unchanged_at_phrasing(self):
        """cbsl_ccpi_20251231.pdf verbatim: 'Food inflation (Y-o-Y) remained
        unchanged at 3.0% in December 2025 ...' -- the 'at' preposition (not
        'to') must also be matched."""
        pdf = FIXTURES_DIR / "cbsl_ccpi_20251231.pdf"
        points = parser.parse_ccpi_pdf(pdf, filename_pub_date=date(2025, 12, 31))
        food = next((p for p in points if p.series_code == parser.SERIES_CCPI_FOOD_YOY), None)
        assert food is not None, "'remained unchanged AT X%' must be matched, not just 'to X%'"
        assert food.value == pytest.approx(3.0)
        assert food.reference_date == date(2025, 12, 1)

    def test_february_2026_decelerated_to_phrasing(self):
        """cbsl_ccpi_20260227.pdf verbatim: 'Food inflation (Y-o-Y)
        decelerated to 0.2% in February\\nY-o-Y Inflation (%) 2.3 1.6\\n2026
        from 3.3% in January 2026, ...' -- another month/year line split,
        this time via an interleaved data-table line rather than a chart
        caption."""
        pdf = FIXTURES_DIR / "cbsl_ccpi_20260227.pdf"
        points = parser.parse_ccpi_pdf(pdf, filename_pub_date=date(2026, 2, 27))
        food = next((p for p in points if p.series_code == parser.SERIES_CCPI_FOOD_YOY), None)
        assert food is not None
        assert food.value == pytest.approx(0.2)
        assert food.reference_date == date(2026, 2, 1)

    def test_base_year_2021_is_never_captured_as_the_reference_year(self):
        """The exact failure the reviewer reproduced: naively widening the
        food-YoY regex's month/year capture window can read the interleaved
        '(2021=100)' base-year caption as the reference YEAR. Assert the
        reference year is never 2021 for a release that is not actually
        about the year 2021 (none of our fixtures are)."""
        cases = [
            ("cbsl_ccpi_20250731.pdf", date(2025, 7, 31)),
            ("cbsl_ccpi_20251231.pdf", date(2025, 12, 31)),
            ("cbsl_ccpi_20260227.pdf", date(2026, 2, 27)),
        ]
        for fname, pubdate in cases:
            points = parser.parse_ccpi_pdf(FIXTURES_DIR / fname, filename_pub_date=pubdate)
            for p in points:
                assert p.reference_date.year != 2021, (
                    f"{fname}: ReferenceDate {p.reference_date} wrongly used the "
                    f"base-year caption's '2021' instead of the release's own year"
                )

    def test_all_recoverable_releases_have_food_yoy(self):
        """Sanity check across every CCPI fixture bundled in this repo: the
        genuinely-recoverable ones (this class's 3 + the original May/June
        2026 fixtures) must all yield CCPI_FOOD_YOY_BASE2021."""
        cases = [
            ("press_20260529_inflation_in_may_2026_ccpi_e.pdf", date(2026, 5, 29)),
            ("press_20260630_inflation_in_june_2026_ccpi_e.pdf", date(2026, 6, 30)),
            ("cbsl_ccpi_20250731.pdf", date(2025, 7, 31)),
            ("cbsl_ccpi_20251231.pdf", date(2025, 12, 31)),
            ("cbsl_ccpi_20260227.pdf", date(2026, 2, 27)),
        ]
        for fname, pubdate in cases:
            points = parser.parse_ccpi_pdf(FIXTURES_DIR / fname, filename_pub_date=pubdate)
            series = {p.series_code for p in points}
            assert parser.SERIES_CCPI_FOOD_YOY in series, f"{fname}: food YoY missing"


class TestMeiParserFixture:
    def test_food_imports_yoy_and_reference_month(self):
        pdf = FIXTURES_DIR / "MEI_202605_e.pdf"
        points = parser.parse_mei_pdf(pdf, pack_yyyymm="202605")
        by_series = {p.series_code: p for p in points}

        food = by_series[parser.SERIES_FOOD_IMPORTS_YOY]
        assert food.value == pytest.approx(56.5)
        # MEI May pack carries APRIL trade data (1-month internal lag) —
        # reference month comes from the table header, not the pack month.
        assert food.reference_date == date(2026, 4, 1)
        assert food.source == "CBSL_MEI"

    def test_policy_rate_uses_pack_own_month(self):
        pdf = FIXTURES_DIR / "MEI_202605_e.pdf"
        points = parser.parse_mei_pdf(pdf, pack_yyyymm="202605")
        by_series = {p.series_code: p for p in points}

        opr = by_series[parser.SERIES_POLICY_RATE]
        assert opr.value == pytest.approx(8.75)
        # Policy rate is an end-of-month snapshot for the PACK's own month
        # (unlike the 1-month-lagged trade data).
        assert opr.reference_date == date(2026, 5, 1)

    def test_mei_creation_date_absent(self):
        """Probe-confirmed: MEI packs carry no /CreationDate metadata key —
        the loader's vintage resolution must fall back for this source."""
        pdf = FIXTURES_DIR / "MEI_202605_e.pdf"
        points = parser.parse_mei_pdf(pdf, pack_yyyymm="202605")
        assert all(p.pdf_creation_date_raw is None for p in points)


# 2. URL / date-pattern tests

class TestCcpiDateExtraction:
    def test_standard_filename(self):
        d = downloader._extract_ccpi_date(
            "cbslweb_documents/press/pr/press_20260630_inflation_in_june_2026_ccpi_e.pdf"
        )
        assert d == date(2026, 6, 30)

    def test_december_variant_filename(self):
        d = downloader._extract_ccpi_date(
            "press_20251231_ccpi_based_inflation_december_2025_e.pdf"
        )
        assert d == date(2025, 12, 31)

    def test_no_date_returns_none(self):
        assert downloader._extract_ccpi_date("some_other_report.pdf") is None

    def test_invalid_date_returns_none(self):
        assert downloader._extract_ccpi_date("press_20261399_x.pdf") is None


class TestMeiMonthExtraction:
    def test_clean_filename(self):
        assert downloader._extract_mei_month("statistics/mei/MEI_202605_e.pdf") == "202605"

    def test_er_suffix_variant(self):
        """'er' suffix variant observed in the wild (MEI_202503_er.pdf)."""
        assert downloader._extract_mei_month("statistics/mei/MEI_202503_er.pdf") == "202503"

    def test_drupal_collision_suffix_variant(self):
        """Drupal filename-collision '_0' suffix observed in the wild
        (MEI_202604_e_0.pdf) — must still resolve to 202604, not be dropped."""
        assert downloader._extract_mei_month("statistics/mei/MEI_202604_e_0.pdf") == "202604"

    def test_invalid_month_returns_none(self):
        assert downloader._extract_mei_month("MEI_202613_e.pdf") is None

    def test_no_match_returns_none(self):
        assert downloader._extract_mei_month("some_other_report.pdf") is None


# 3. Cache-filename sanitization — derived from PARSED date, never URL basename

class TestCacheFilenameSanitization:
    def test_ccpi_cache_filename_from_parsed_date_not_url(self, tmp_path, monkeypatch):
        monkeypatch.setattr(netguard, "_resolve_ips", lambda host: ["8.8.8.8"])
        session = MagicMock()
        resp = MagicMock()
        resp.__enter__ = MagicMock(return_value=resp)
        resp.__exit__ = MagicMock(return_value=False)
        resp.raise_for_status = MagicMock(return_value=None)
        resp.is_redirect = False
        resp.is_permanent_redirect = False
        resp.headers = {}
        body = b"%PDF-1.4 fake" * 100
        resp.iter_content = MagicMock(return_value=iter([body]))
        session.get = MagicMock(return_value=resp)

        links = [downloader.CcpiLink(
            pub_date=date(2026, 6, 30),
            url="https://www.cbsl.gov.lk/sites/default/files/x/press_WEIRD_name(1).pdf",
        )]
        cache_dir = tmp_path / "cbsl_cache"
        result = downloader.download_ccpi_pdfs(cache_dir, links, session=session, delay=0)

        assert len(result) == 1
        _pub_date, local_path, _digest = result[0]
        assert local_path.name == "cbsl_ccpi_20260630.pdf", (
            "Cache filename must be derived from the PARSED date, never the "
            "URL basename (which here is deliberately a weird/adversarial name)"
        )

    def test_mei_cache_filename_from_parsed_month_not_url(self, tmp_path, monkeypatch):
        monkeypatch.setattr(netguard, "_resolve_ips", lambda host: ["8.8.8.8"])
        session = MagicMock()
        resp = MagicMock()
        resp.__enter__ = MagicMock(return_value=resp)
        resp.__exit__ = MagicMock(return_value=False)
        resp.raise_for_status = MagicMock(return_value=None)
        resp.is_redirect = False
        resp.is_permanent_redirect = False
        resp.headers = {}
        body = b"%PDF-1.4 fake" * 100
        resp.iter_content = MagicMock(return_value=iter([body]))
        session.get = MagicMock(return_value=resp)

        links = [downloader.MeiLink(
            pack_yyyymm="202605",
            url="https://www.cbsl.gov.lk/sites/default/files/x/MEI_202604_e_0.pdf",
        )]
        cache_dir = tmp_path / "cbsl_cache"
        result = downloader.download_mei_pdfs(cache_dir, links, session=session, delay=0)

        assert len(result) == 1
        _pack, local_path, _digest = result[0]
        assert local_path.name == "cbsl_mei_202605.pdf"


# 3b. Duplicate-period artifact dedup (real bug found during live
#     verification: CBSL served MEI_201909_e_0.pdf AND MEI_201909_e_1.pdf,
#     both resolving to pack_yyyymm='201909', which collided on the DB's
#     unique index and aborted the whole batch transaction).

class TestDuplicatePeriodDedup:
    def test_dedup_by_key_keeps_first_drops_rest(self):
        links = [
            downloader.MeiLink(pack_yyyymm="201909", url="https://www.cbsl.gov.lk/x/MEI_201909_e_0.pdf"),
            downloader.MeiLink(pack_yyyymm="201909", url="https://www.cbsl.gov.lk/x/MEI_201909_e_1.pdf"),
            downloader.MeiLink(pack_yyyymm="201910", url="https://www.cbsl.gov.lk/x/MEI_201910_e.pdf"),
        ]
        kept, n_dropped = downloader._dedup_by_key(links, lambda l: l.pack_yyyymm)
        assert n_dropped == 1
        assert [l.pack_yyyymm for l in kept] == ["201909", "201910"]
        assert kept[0].url.endswith("_e_0.pdf"), "must keep the FIRST-seen URL for a duplicate period"

    def test_download_mei_pdfs_never_returns_two_entries_for_same_month(self, tmp_path, monkeypatch):
        monkeypatch.setattr(netguard, "_resolve_ips", lambda host: ["8.8.8.8"])
        session = MagicMock()
        resp = MagicMock()
        resp.__enter__ = MagicMock(return_value=resp)
        resp.__exit__ = MagicMock(return_value=False)
        resp.raise_for_status = MagicMock(return_value=None)
        resp.is_redirect = False
        resp.is_permanent_redirect = False
        resp.headers = {}
        body = b"%PDF-1.4 fake" * 100
        resp.iter_content = MagicMock(return_value=iter([body]))
        session.get = MagicMock(return_value=resp)

        links = [
            downloader.MeiLink(pack_yyyymm="201909", url="https://www.cbsl.gov.lk/x/MEI_201909_e_0.pdf"),
            downloader.MeiLink(pack_yyyymm="201909", url="https://www.cbsl.gov.lk/x/MEI_201909_e_1.pdf"),
        ]
        result = downloader.download_mei_pdfs(tmp_path / "c", links, session=session, delay=0)

        months = [m for m, _p, _d in result]
        assert months == ["201909"], "duplicate-period artifacts must collapse to exactly one cache entry"
        assert session.get.call_count == 1, "the dropped duplicate must never even be fetched"

    def test_dedup_by_key_noop_when_all_unique(self):
        links = [
            downloader.CcpiLink(pub_date=date(2026, 1, 1), url="a"),
            downloader.CcpiLink(pub_date=date(2026, 2, 1), url="b"),
        ]
        kept, n_dropped = downloader._dedup_by_key(links, lambda l: l.pub_date)
        assert n_dropped == 0
        assert len(kept) == 2


class TestLoaderInBatchDedup:
    """Defensive in-batch dedup in upsert_macro_points — belt-and-suspenders
    against the same failure mode the downloader-level fix addresses."""

    def test_in_batch_duplicate_collapses_instead_of_crashing(self):
        p1 = parser.ParsedMacroPoint(
            series_code="FOOD_IMPORTS_YOY", reference_date=date(2019, 8, 1), value=6.5,
            source="CBSL_MEI", pdf_creation_date_raw="D:20201023092051+05'30'",
        )
        p2 = parser.ParsedMacroPoint(
            series_code="FOOD_IMPORTS_YOY", reference_date=date(2019, 8, 1), value=6.5,
            source="CBSL_MEI", pdf_creation_date_raw="D:20201023092051+05'30'",
        )
        counters = loader.upsert_macro_points([p1, p2], dry_run=True)
        assert counters["inserted"] == 1, (
            "two points resolving to the identical (SeriesCode, ReferenceDate, "
            "PublishedAt) key must collapse to exactly one row, not attempt two "
            "inserts of the same key"
        )


# 4. Page-count cap

class TestPageCountCap:
    def test_ccpi_parser_rejects_pdf_over_page_cap(self, monkeypatch):
        fake_pdf = MagicMock()
        fake_pdf.metadata = {}
        fake_pdf.pages = [MagicMock() for _ in range(parser._MAX_PAGES + 1)]
        fake_pdf.__enter__ = MagicMock(return_value=fake_pdf)
        fake_pdf.__exit__ = MagicMock(return_value=False)

        with patch("pdfplumber.open", return_value=fake_pdf):
            result = parser._parse_ccpi_pdf_impl(Path("huge.pdf"), filename_pub_date=date(2026, 1, 1))

        assert result == []

    def test_mei_parser_rejects_pdf_over_page_cap(self, monkeypatch):
        fake_pdf = MagicMock()
        fake_pdf.metadata = {}
        fake_pdf.pages = [MagicMock() for _ in range(parser._MAX_PAGES + 1)]
        fake_pdf.__enter__ = MagicMock(return_value=fake_pdf)
        fake_pdf.__exit__ = MagicMock(return_value=False)

        with patch("pdfplumber.open", return_value=fake_pdf):
            result = parser._parse_mei_pdf_impl(Path("huge.pdf"), pack_yyyymm="202601")

        assert result == []

    def test_real_fixtures_are_under_the_cap(self):
        """Sanity check: the real fixtures used elsewhere in this file must
        themselves be under the cap (else every other test here is vacuous)."""
        import pdfplumber
        for name in ("press_20260630_inflation_in_june_2026_ccpi_e.pdf", "MEI_202605_e.pdf"):
            with pdfplumber.open(str(FIXTURES_DIR / name)) as pdf:
                assert len(pdf.pages) <= parser._MAX_PAGES


# 5. PublishedAt resolution: CCPI cross-check + MEI conservative-late rule

class TestCcpiPublishedAtResolution:
    def test_url_date_and_creation_date_agree(self):
        p = parser.ParsedMacroPoint(
            series_code="X", reference_date=date(2026, 6, 1), value=1.0,
            source="CBSL_CCPI",
            pdf_creation_date_raw="D:20260630163101+05'30'",
            filename_pub_date=date(2026, 6, 30),
        )
        published_at, imputed = loader.resolve_published_at(p)
        assert published_at == date(2026, 6, 30)
        assert imputed is False

    def test_disagreement_uses_the_later_date(self, caplog):
        """If the URL date and /CreationDate disagree, the LATER wins (never
        the earlier — erring late only delays a join, erring early leaks)."""
        p = parser.ParsedMacroPoint(
            series_code="X", reference_date=date(2026, 6, 1), value=1.0,
            source="CBSL_CCPI",
            pdf_creation_date_raw="D:20260701090000+05'30'",  # later than URL date
            filename_pub_date=date(2026, 6, 30),
        )
        published_at, imputed = loader.resolve_published_at(p)
        assert published_at == date(2026, 7, 1)
        assert imputed is False

    def test_disagreement_logs_a_warning(self, caplog):
        import logging
        caplog.set_level(logging.WARNING)
        p = parser.ParsedMacroPoint(
            series_code="X", reference_date=date(2026, 6, 1), value=1.0,
            source="CBSL_CCPI",
            pdf_creation_date_raw="D:20260629090000+05'30'",  # earlier than URL date
            filename_pub_date=date(2026, 6, 30),
        )
        published_at, _imputed = loader.resolve_published_at(p)
        assert published_at == date(2026, 6, 30)  # later of the two
        assert any("disagree" in r.message for r in caplog.records)

    def test_missing_creation_date_falls_back_to_url_date(self):
        p = parser.ParsedMacroPoint(
            series_code="X", reference_date=date(2026, 6, 1), value=1.0,
            source="CBSL_CCPI",
            pdf_creation_date_raw=None,
            filename_pub_date=date(2026, 6, 30),
        )
        published_at, imputed = loader.resolve_published_at(p)
        assert published_at == date(2026, 6, 30)
        assert imputed is False


class TestMeiPublishedAtResolution:
    def test_no_creation_date_no_listing_date_imputes_conservatively_late(self):
        p = parser.ParsedMacroPoint(
            series_code="FOOD_IMPORTS_YOY", reference_date=date(2026, 4, 1), value=1.0,
            source="CBSL_MEI",
            pdf_creation_date_raw=None,
            filename_pub_date=None,
        )
        published_at, imputed = loader.resolve_published_at(p)
        assert imputed is True
        assert published_at > date(2026, 4, 1), (
            "Imputed PublishedAt must be AFTER ReferenceDate — never default to it"
        )

    def test_never_defaults_published_at_to_reference_date_exactly(self):
        """The anti-conservative failure mode this whole vintage policy exists
        to prevent: PublishedAt must never equal ReferenceDate when no real
        vintage signal exists (that would be lookahead)."""
        p = parser.ParsedMacroPoint(
            series_code="FOOD_IMPORTS_YOY", reference_date=date(2026, 4, 1), value=1.0,
            source="CBSL_MEI", pdf_creation_date_raw=None, filename_pub_date=None,
        )
        published_at, _imputed = loader.resolve_published_at(p)
        assert published_at != p.reference_date

    def test_creation_date_present_used_and_not_imputed(self):
        p = parser.ParsedMacroPoint(
            series_code="FOOD_IMPORTS_YOY", reference_date=date(2026, 4, 1), value=1.0,
            source="CBSL_MEI",
            pdf_creation_date_raw="D:20260601090000+05'30'",
            filename_pub_date=None,
        )
        published_at, imputed = loader.resolve_published_at(p)
        assert published_at == date(2026, 6, 1)
        assert imputed is False

    def test_listing_date_used_when_creation_date_absent(self):
        p = parser.ParsedMacroPoint(
            series_code="FOOD_IMPORTS_YOY", reference_date=date(2026, 4, 1), value=1.0,
            source="CBSL_MEI", pdf_creation_date_raw=None, filename_pub_date=None,
        )
        published_at, imputed = loader.resolve_published_at(p, listing_date=date(2026, 5, 20))
        assert published_at == date(2026, 5, 20)
        assert imputed is False


# 6. SHA-256 recorded per downloaded artifact

class TestSha256Recorded:
    def test_sha256_hex_matches_hashlib(self):
        import hashlib
        data = b"hello cbsl"
        assert downloader.sha256_hex(data) == hashlib.sha256(data).hexdigest()

    def test_download_ccpi_pdfs_returns_sha256_per_artifact(self, tmp_path, monkeypatch):
        monkeypatch.setattr(netguard, "_resolve_ips", lambda host: ["8.8.8.8"])
        session = MagicMock()
        resp = MagicMock()
        resp.__enter__ = MagicMock(return_value=resp)
        resp.__exit__ = MagicMock(return_value=False)
        resp.raise_for_status = MagicMock(return_value=None)
        resp.is_redirect = False
        resp.is_permanent_redirect = False
        resp.headers = {}
        body = b"%PDF-1.4 fake" * 100
        resp.iter_content = MagicMock(return_value=iter([body]))
        session.get = MagicMock(return_value=resp)

        links = [downloader.CcpiLink(pub_date=date(2026, 1, 1), url="https://www.cbsl.gov.lk/x.pdf")]
        result = downloader.download_ccpi_pdfs(tmp_path / "c", links, session=session, delay=0)

        import hashlib
        assert result[0][2] == hashlib.sha256(body).hexdigest()


# 7. netguard: cbsl.gov.lk allowed, statistics.gov.lk NOT allowed

class TestNetguardAllowlist:
    def test_cbsl_gov_lk_is_allowed(self):
        netguard.assert_host_allowed("https://www.cbsl.gov.lk/en/measures-of-consumer-price-inflation")

    def test_cbsl_subdomain_allowed(self):
        netguard.assert_host_allowed("https://cbsl.gov.lk/some/path")

    def test_statistics_gov_lk_is_not_allowed(self):
        """NCPI (DCS) was scope-cut in the 2026-07-04 P3 decision specifically
        so the allowlist stays single-apex — statistics.gov.lk must NOT be on
        the default allowlist."""
        with pytest.raises(netguard.DisallowedUrlError):
            netguard.assert_host_allowed("https://www.statistics.gov.lk/some/path")


# 8. Route registered on admin_router + auth test (mirrors HARTI's route test)

class TestAdminRouteAuth:
    """Mirrors tests/test_admin_auth.py's pattern for /admin/ingest-harti,
    applied to /admin/ingest-cbsl-macro."""

    ROUTE = "/admin/ingest-cbsl-macro"
    ENV_VAR = "ML_ADMIN_API_KEY"
    CORRECT_KEY = "test-secret-key-12345"
    WRONG_KEY = "not-the-right-key"

    @staticmethod
    def _client():
        # Stub agriforecast_ml.serving.predict BEFORE importing app, same
        # pattern test_admin_auth.py uses (predict.py does DB access at
        # module import time via registry.load_promoted()).
        from types import ModuleType
        if "agriforecast_ml.serving.predict" not in sys.modules:
            stub = ModuleType("agriforecast_ml.serving.predict")
            stub.model_info = MagicMock(return_value={"version": "v-test"})
            stub.predict_harvest = MagicMock(return_value={})
            stub.timeline = MagicMock(return_value={})
            sys.modules["agriforecast_ml.serving.predict"] = stub

        from agriforecast_ml.serving.app import app
        from starlette.testclient import TestClient
        return TestClient(app, raise_server_exceptions=False)

    def test_route_is_registered(self):
        client = self._client()
        # Missing auth still proves the route EXISTS (401, not 404).
        resp = client.post(self.ROUTE, json={})
        assert resp.status_code != 404, "route must be registered on admin_router"

    def test_no_key_returns_401(self, monkeypatch):
        monkeypatch.setenv(self.ENV_VAR, self.CORRECT_KEY)
        client = self._client()
        resp = client.post(self.ROUTE, json={})
        assert resp.status_code == 401

    def test_wrong_key_returns_401(self, monkeypatch):
        monkeypatch.setenv(self.ENV_VAR, self.CORRECT_KEY)
        client = self._client()
        resp = client.post(self.ROUTE, json={}, headers={"X-API-Key": self.WRONG_KEY})
        assert resp.status_code == 401

    def test_correct_key_with_stubbed_pipeline_returns_200(self, monkeypatch):
        monkeypatch.setenv(self.ENV_VAR, self.CORRECT_KEY)
        client = self._client()

        cbsl_stub = MagicMock()
        cbsl_stub.run = MagicMock(return_value={
            "status": "ok", "artifactsFetched": 0, "artifactsSkipped": 0,
            "rowsInserted": 0, "rowsUpdated": 0, "rowsSkippedInvalid": 0,
            "perSeriesCoverage": {},
        })

        with patch.dict(sys.modules, {"ingest_cbsl_macro": cbsl_stub}):
            resp = client.post(
                self.ROUTE, json={"dryRun": True},
                headers={"X-API-Key": self.CORRECT_KEY},
            )

        assert resp.status_code == 200, resp.text
        assert resp.json().get("status") == "ok"

    def test_import_failure_returns_503_generic_detail(self, monkeypatch):
        """Import-failure -> 503, generic detail (never interpolate the raw
        exception into the response body)."""
        monkeypatch.setenv(self.ENV_VAR, self.CORRECT_KEY)
        client = self._client()

        with patch.dict(sys.modules, {"ingest_cbsl_macro": None}):
            resp = client.post(
                self.ROUTE, json={}, headers={"X-API-Key": self.CORRECT_KEY},
            )

        assert resp.status_code == 503
        detail = resp.json().get("detail", "")
        assert "ingest_cbsl_macro" not in detail.lower()
        assert "traceback" not in detail.lower()

    def test_pipeline_failure_returns_502_generic_detail(self, monkeypatch):
        monkeypatch.setenv(self.ENV_VAR, self.CORRECT_KEY)
        client = self._client()

        cbsl_stub = MagicMock()
        cbsl_stub.run = MagicMock(side_effect=RuntimeError("some secret internal detail"))

        with patch.dict(sys.modules, {"ingest_cbsl_macro": cbsl_stub}):
            resp = client.post(
                self.ROUTE, json={}, headers={"X-API-Key": self.CORRECT_KEY},
            )

        assert resp.status_code == 502
        detail = resp.json().get("detail", "")
        assert "some secret internal detail" not in detail


# 9. Watermark incremental design (2026-08-16 OOM/staleness fix, ClickUp
#    fix/cbsl-macro-incremental). See ingest_cbsl_macro.py's module docstring
#    for the full incident writeup.

def _mock_engine(connect_payloads: list) -> "tuple[MagicMock, MagicMock]":
    """Mirrors test_ingest_verify.py::_mock_engine / test_dec_mirror.py's
    original. `connect_payloads` is a list of dicts, one per successive
    `engine.connect()...execute()` call (in call order), shaped
    {"scalar": v}. `conn.execute.call_log` records (stmt_text, params) per
    call, in order -- so a test can assert on the actual SQL text, not just
    the mocked return value (MINOR 5, 2026-08-17 review: without this, a
    MAX-> MIN mutation in the query text is invisible to every test)."""
    engine = MagicMock()
    conn = MagicMock()
    state = {"n": 0}
    calls: list = []

    def _connect_execute(stmt, params=None):
        idx = state["n"]
        state["n"] += 1
        calls.append((str(stmt), params))
        res = MagicMock()
        payload = connect_payloads[idx] if idx < len(connect_payloads) else {}
        res.scalar.return_value = payload.get("scalar")
        return res

    conn.execute.side_effect = _connect_execute
    conn.execute.call_log = calls
    engine.connect.return_value.__enter__.return_value = conn
    return engine, conn


class TestMonthArithmeticHelpers:
    """Pure month-shift helpers -- exact pins, including the year-boundary
    case (the class of bug most likely to silently miscount)."""

    def test_month_shift_date_within_year(self):
        assert downloader._month_shift_date(date(2026, 6, 30), -1) == date(2026, 5, 1)

    def test_month_shift_date_crosses_year_boundary(self):
        assert downloader._month_shift_date(date(2026, 1, 15), -1) == date(2025, 12, 1)

    def test_month_shift_date_zero_delta_floors_to_first_of_month(self):
        assert downloader._month_shift_date(date(2026, 6, 30), 0) == date(2026, 6, 1)

    def test_shift_yyyymm_within_year(self):
        assert downloader._shift_yyyymm("202605", -1) == "202604"

    def test_shift_yyyymm_crosses_year_boundary(self):
        assert downloader._shift_yyyymm("202601", -1) == "202512"

    def test_shift_yyyymm_positive_delta(self):
        assert downloader._shift_yyyymm("202512", 1) == "202601"


class TestCcpiWatermarkFilter:
    LINKS = [
        downloader.CcpiLink(pub_date=date(2026, 4, 30), url="apr"),
        downloader.CcpiLink(pub_date=date(2026, 5, 29), url="may"),
        downloader.CcpiLink(pub_date=date(2026, 6, 30), url="jun"),
    ]

    def test_none_watermark_keeps_everything(self):
        """Empty DB / --full: nothing is dropped."""
        kept, n_skipped = downloader.filter_ccpi_since(self.LINKS, lambda l: l.pub_date, None)
        assert n_skipped == 0
        assert len(kept) == 3

    def test_watermark_at_latest_keeps_one_month_overlap(self):
        """Watermark == June: April (2 months back) is dropped, May+June
        (within the 1-month overlap) are kept."""
        kept, n_skipped = downloader.filter_ccpi_since(
            self.LINKS, lambda l: l.pub_date, date(2026, 6, 30)
        )
        assert n_skipped == 1
        assert [l.url for l in kept] == ["may", "jun"]

    def test_overlap_boundary_is_inclusive_of_the_overlap_month(self):
        """The artifact exactly `overlap_months` before the watermark's month
        must be KEPT (it is the whole point of the safety overlap), not
        dropped -- an off-by-one here would silently narrow the overlap to
        zero."""
        watermark = date(2026, 6, 30)
        threshold_month_link = downloader.CcpiLink(pub_date=date(2026, 5, 1), url="boundary")
        kept, n_skipped = downloader.filter_ccpi_since(
            [threshold_month_link], lambda l: l.pub_date, watermark
        )
        assert n_skipped == 0
        assert kept == [threshold_month_link]

    def test_one_day_before_the_overlap_boundary_is_dropped(self):
        watermark = date(2026, 6, 30)
        just_before = downloader.CcpiLink(pub_date=date(2026, 4, 30), url="just_before")
        kept, n_skipped = downloader.filter_ccpi_since(
            [just_before], lambda l: l.pub_date, watermark
        )
        assert n_skipped == 1
        assert kept == []

    def test_reusable_against_cache_glob_tuples_not_just_link_objects(self):
        """The same function must work against (date, Path) tuples from the
        --no-download cache-glob branch, not only CcpiLink objects -- this is
        the mechanism that stops an ephemeral k8s cache from forcing a full
        re-download every run."""
        cache_tuples = [(date(2026, 4, 30), Path("a.pdf")), (date(2026, 6, 30), Path("b.pdf"))]
        kept, n_skipped = downloader.filter_ccpi_since(
            cache_tuples, lambda t: t[0], date(2026, 6, 30)
        )
        assert n_skipped == 1
        assert kept == [(date(2026, 6, 30), Path("b.pdf"))]


class TestMeiWatermarkFilter:
    LINKS = [
        downloader.MeiLink(pack_yyyymm="202604", url="apr"),
        downloader.MeiLink(pack_yyyymm="202605", url="may"),
        downloader.MeiLink(pack_yyyymm="202606", url="jun"),
    ]

    def test_none_watermark_keeps_everything(self):
        kept, n_skipped = downloader.filter_mei_since(self.LINKS, lambda l: l.pack_yyyymm, None)
        assert n_skipped == 0
        assert len(kept) == 3

    def test_watermark_at_latest_keeps_one_month_overlap(self):
        kept, n_skipped = downloader.filter_mei_since(
            self.LINKS, lambda l: l.pack_yyyymm, "202606"
        )
        assert n_skipped == 1
        assert [l.url for l in kept] == ["may", "jun"]

    def test_year_boundary_watermark(self):
        """Watermark pack=202601 (January): threshold must roll back into the
        PRIOR year (202512), not wrap incorrectly."""
        links = [
            downloader.MeiLink(pack_yyyymm="202511", url="nov"),
            downloader.MeiLink(pack_yyyymm="202512", url="dec"),
            downloader.MeiLink(pack_yyyymm="202601", url="jan"),
        ]
        kept, n_skipped = downloader.filter_mei_since(links, lambda l: l.pack_yyyymm, "202601")
        assert n_skipped == 1
        assert [l.url for l in kept] == ["dec", "jan"]


class TestNetworkPathAndCacheGlobWiring:
    """BLOCKER 1 (2026-08-17 review): the pure downloader.filter_*_since()
    functions above are correct in isolation, but run() must actually CALL
    them at all four sites -- CCPI download, CCPI cache-glob, MEI download,
    MEI cache-glob. Every earlier run()-level test used no_download=True
    with only CCPI cache files, so a mutation deleting the filter call at
    the CCPI download branch, the MEI download branch, or the MEI
    cache-glob branch survived the entire suite (only the CCPI cache-glob
    site was exercised, via TestFullFlagOverride). These tests close all
    three gaps."""

    def test_network_path_download_functions_receive_only_post_watermark_links(self, tmp_path, monkeypatch):
        """The CronJob's real path is no_download=False. Mocks the four
        network-touching functions and asserts BOTH download_ccpi_pdfs and
        download_mei_pdfs are called with ONLY the post-watermark link,
        proving the filter runs BEFORE download on both sources."""
        ccpi_links_all = [
            downloader.CcpiLink(pub_date=date(2026, 4, 30), url="old_ccpi"),
            downloader.CcpiLink(pub_date=date(2026, 6, 30), url="new_ccpi"),
        ]
        mei_links_all = [
            downloader.MeiLink(pack_yyyymm="202604", url="old_mei"),
            downloader.MeiLink(pack_yyyymm="202606", url="new_mei"),
        ]
        watermarks = loader.MacroWatermarks(ccpi_published_at=date(2026, 6, 30), mei_pack_yyyymm="202606")

        monkeypatch.setattr(loader, "get_watermarks", lambda *a, **k: watermarks)
        monkeypatch.setattr(downloader, "scrape_ccpi_links", lambda session: ccpi_links_all)
        monkeypatch.setattr(downloader, "scrape_mei_links", lambda session: mei_links_all)

        download_ccpi_calls: list = []
        download_mei_calls: list = []

        def fake_download_ccpi(cache_dir, links, session=None):
            download_ccpi_calls.append(list(links))
            return []

        def fake_download_mei(cache_dir, links, session=None):
            download_mei_calls.append(list(links))
            return []

        monkeypatch.setattr(downloader, "download_ccpi_pdfs", fake_download_ccpi)
        monkeypatch.setattr(downloader, "download_mei_pdfs", fake_download_mei)

        summary = ingest_cbsl_macro.run(cache_dir=tmp_path / "cbsl_cache", no_download=False)

        assert len(download_ccpi_calls) == 1
        assert [l.url for l in download_ccpi_calls[0]] == ["new_ccpi"], (
            "download_ccpi_pdfs must receive ONLY the post-watermark CCPI "
            "link -- the filter must run BEFORE download, at the download "
            "branch, not be skipped there"
        )
        assert len(download_mei_calls) == 1
        assert [l.url for l in download_mei_calls[0]] == ["new_mei"], (
            "download_mei_pdfs must receive ONLY the post-watermark MEI link "
            "-- the filter must run BEFORE download, at the MEI download "
            "branch, not be skipped there"
        )
        assert summary["artifactsWatermarkSkipped"] == 2, "one dropped artifact per source"

    def test_mei_cache_glob_watermark_skip_is_wired(self, tmp_path, monkeypatch):
        """CCPI's equivalent of this test is
        TestFullFlagOverride.test_full_flag_processes_artifacts_the_watermark_would_have_dropped
        (the ONE site the reviewer found already pinned). This is the
        missing MEI cache-glob counterpart -- no prior test ever wrote an
        MEI file into the --no-download cache and checked it against a real
        watermark."""
        cache_dir = tmp_path / "cbsl_cache"
        cache_dir.mkdir(parents=True, exist_ok=True)
        # Old pack, more than the 1-month overlap behind the watermark below -- must be dropped.
        (cache_dir / "cbsl_mei_202604.pdf").write_bytes(b"%PDF-1.4 fake")

        def fake_parse_mei(pdf_path, *, pack_yyyymm=None):
            return [parser.ParsedMacroPoint(
                series_code=parser.SERIES_POLICY_RATE,
                reference_date=date(int(pack_yyyymm[:4]), int(pack_yyyymm[4:6]), 1),
                value=8.0, source="CBSL_MEI",
            )]

        monkeypatch.setattr(parser, "parse_mei_pdf", fake_parse_mei)
        monkeypatch.setattr(
            loader, "upsert_macro_points",
            lambda points, dry_run=False: {"inserted": len(points), "updated": 0, "skipped_invalid": 0},
        )
        watermarks = loader.MacroWatermarks(ccpi_published_at=None, mei_pack_yyyymm="202606")
        monkeypatch.setattr(loader, "get_watermarks", lambda *a, **k: watermarks)

        summary = ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True, full=False)

        assert summary["artifactsWatermarkSkipped"] == 1, (
            "the MEI cache-glob branch must apply filter_mei_since BEFORE "
            "parsing -- an artifact older than the overlap window must be "
            "dropped, not parsed"
        )
        assert summary["artifactsFetched"] == 0
        assert summary["rowsInserted"] == 0

    def test_mei_cache_glob_keeps_artifact_inside_the_overlap_window(self, tmp_path, monkeypatch):
        """Same setup, but the cached pack is WITHIN the 1-month overlap --
        must be parsed, not dropped (proves the wiring isn't just always
        dropping everything)."""
        cache_dir = tmp_path / "cbsl_cache"
        cache_dir.mkdir(parents=True, exist_ok=True)
        (cache_dir / "cbsl_mei_202606.pdf").write_bytes(b"%PDF-1.4 fake")

        def fake_parse_mei(pdf_path, *, pack_yyyymm=None):
            return [parser.ParsedMacroPoint(
                series_code=parser.SERIES_POLICY_RATE,
                reference_date=date(int(pack_yyyymm[:4]), int(pack_yyyymm[4:6]), 1),
                value=8.0, source="CBSL_MEI",
            )]

        monkeypatch.setattr(parser, "parse_mei_pdf", fake_parse_mei)
        monkeypatch.setattr(
            loader, "upsert_macro_points",
            lambda points, dry_run=False: {"inserted": len(points), "updated": 0, "skipped_invalid": 0},
        )
        watermarks = loader.MacroWatermarks(ccpi_published_at=None, mei_pack_yyyymm="202606")
        monkeypatch.setattr(loader, "get_watermarks", lambda *a, **k: watermarks)

        summary = ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True, full=False)

        assert summary["artifactsWatermarkSkipped"] == 0
        assert summary["artifactsFetched"] == 1
        assert summary["rowsInserted"] == 1


class TestGetWatermarks:
    """Hermetic -- a mocked SQLAlchemy engine, no live DB (mirrors
    test_ingest_verify.py's `_mock_engine` pattern)."""

    def test_empty_db_returns_none_for_both(self):
        engine, _conn = _mock_engine([{"scalar": None}, {"scalar": None}, {"scalar": None}])
        wm = loader.get_watermarks(engine=engine)
        assert wm.ccpi_published_at is None
        assert wm.mei_pack_yyyymm is None

    def test_full_coverage_combines_ccpi_and_mei_correctly(self):
        # order: ccpi MAX(PublishedAt), MAX(ReferenceDate) OPR, MAX(ReferenceDate) FOOD_IMPORTS
        engine, _conn = _mock_engine([
            {"scalar": date(2026, 6, 30)},
            {"scalar": date(2026, 5, 1)},   # OPR ReferenceDate == pack month directly
            {"scalar": date(2026, 4, 1)},   # FOOD_IMPORTS ReferenceDate == pack month - 1
        ])
        wm = loader.get_watermarks(engine=engine)
        assert wm.ccpi_published_at == date(2026, 6, 30)
        assert wm.mei_pack_yyyymm == "202605", (
            "OPR ReferenceDate maps directly to the pack month; FOOD_IMPORTS "
            "ReferenceDate + 1 month must resolve to the SAME pack (202605), "
            "matching the real live-DB numbers verified pre-fix"
        )

    def test_opr_missing_falls_back_to_food_derived_pack(self):
        engine, _conn = _mock_engine([
            {"scalar": None},
            {"scalar": None},               # OPR absent (e.g. section parse miss)
            {"scalar": date(2026, 4, 1)},    # FOOD_IMPORTS present -> pack 202605
        ])
        wm = loader.get_watermarks(engine=engine)
        assert wm.mei_pack_yyyymm == "202605"

    def test_food_missing_falls_back_to_opr_derived_pack(self):
        engine, _conn = _mock_engine([
            {"scalar": None},
            {"scalar": date(2026, 5, 1)},    # OPR present -> pack 202605
            {"scalar": None},
        ])
        wm = loader.get_watermarks(engine=engine)
        assert wm.mei_pack_yyyymm == "202605"

    def test_takes_the_max_of_the_two_pack_candidates_not_the_first(self):
        """A stale OPR (e.g. many packs never had a parseable OPR section)
        must never understate the watermark below what FOOD_IMPORTS alone
        already proves is covered."""
        engine, _conn = _mock_engine([
            {"scalar": None},
            {"scalar": date(2025, 11, 1)},   # OPR -> pack 202511 (stale)
            {"scalar": date(2026, 4, 1)},    # FOOD_IMPORTS -> pack 202605 (fresher)
        ])
        wm = loader.get_watermarks(engine=engine)
        assert wm.mei_pack_yyyymm == "202605"

    def test_string_and_datetime_scalars_are_coerced_to_date(self):
        """Some driver/dialect paths return str or datetime instead of a bare
        date -- both must coerce correctly, not raise or silently misparse."""
        engine, _conn = _mock_engine([
            {"scalar": "2026-06-30"},
            {"scalar": datetime(2026, 5, 1, 0, 0, 0)},
            {"scalar": None},
        ])
        wm = loader.get_watermarks(engine=engine)
        assert wm.ccpi_published_at == date(2026, 6, 30)
        assert wm.mei_pack_yyyymm == "202605"

    # -- MINOR 5 (2026-08-17 review): _mock_engine was blind to SQL text, so
    # a MAX -> MIN mutation in get_watermarks() would survive the whole
    # suite. Pin the actual query text for all three queries.

    def test_all_three_watermark_queries_use_max_not_something_else(self):
        engine, conn = _mock_engine([{"scalar": None}, {"scalar": None}, {"scalar": None}])
        loader.get_watermarks(engine=engine)

        assert len(conn.execute.call_log) == 3, "CCPI PublishedAt + OPR ReferenceDate + FOOD_IMPORTS ReferenceDate"
        for stmt_text, _params in conn.execute.call_log:
            assert "MAX(" in stmt_text.upper(), (
                f"watermark query must use MAX(), got: {stmt_text!r} -- a "
                "MIN() mutation here would silently make the watermark the "
                "OLDEST known row instead of the newest, re-scanning "
                "(nearly) everything forever without ever erroring"
            )

    def test_ccpi_query_targets_publishedat_mei_queries_target_referencedate(self):
        """A coarser sanity pin than the MAX() check alone: catches a
        mutation that swaps PublishedAt for ReferenceDate (or vice versa) on
        the wrong query, which MAX(...) alone would not catch."""
        engine, conn = _mock_engine([{"scalar": None}, {"scalar": None}, {"scalar": None}])
        loader.get_watermarks(engine=engine)

        ccpi_stmt, opr_stmt, food_stmt = (s for s, _p in conn.execute.call_log)
        assert "PUBLISHEDAT" in ccpi_stmt.upper()
        assert "REFERENCEDATE" in opr_stmt.upper()
        assert "REFERENCEDATE" in food_stmt.upper()

    # -- MAJOR 3 (2026-08-17 review): watermark poisonable into the future.
    # parser.py's last-resort MEI fallback, or a garbage CCPI /CreationDate,
    # can push a watermark past "now" -- unclamped, that silently and
    # PERMANENTLY drops every future artifact (the watermark filter never
    # catches back up on its own). Both fields must clamp to `now`.

    def test_ccpi_published_at_is_clamped_to_today(self):
        fixed_today = date(2026, 8, 17)
        engine, _conn = _mock_engine([
            {"scalar": date(2027, 1, 1)},   # garbage /CreationDate, a year in the future
            {"scalar": None},
            {"scalar": None},
        ])
        wm = loader.get_watermarks(engine=engine, now=fixed_today)
        assert wm.ccpi_published_at == fixed_today, (
            "a future-dated CCPI watermark must clamp to today, or CCPI "
            "ingestion silently stops forever from that point on"
        )

    def test_ccpi_published_at_at_or_before_today_is_never_clamped(self):
        fixed_today = date(2026, 8, 17)
        engine, _conn = _mock_engine([
            {"scalar": date(2026, 6, 30)},
            {"scalar": None},
            {"scalar": None},
        ])
        wm = loader.get_watermarks(engine=engine, now=fixed_today)
        assert wm.ccpi_published_at == date(2026, 6, 30), "a genuinely past watermark must be untouched"

    def test_mei_pack_is_clamped_to_the_current_month(self):
        fixed_today = date(2026, 8, 17)
        engine, _conn = _mock_engine([
            {"scalar": None},
            {"scalar": date(2026, 10, 1)},  # OPR ReferenceDate -> pack 202610, 2 months in the future
            {"scalar": None},
        ])
        wm = loader.get_watermarks(engine=engine, now=fixed_today)
        assert wm.mei_pack_yyyymm == "202608", (
            "a future-pack MEI watermark must clamp to the current month "
            "(202608), or that pack's real successor is silently dropped "
            "forever -- the exact live failure mode probed against "
            "parser._mei_reference_month's last-resort fallback"
        )

    def test_mei_pack_at_or_before_current_month_is_never_clamped(self):
        fixed_today = date(2026, 8, 17)
        engine, _conn = _mock_engine([
            {"scalar": None},
            {"scalar": date(2026, 5, 1)},   # pack 202605, genuinely in the past
            {"scalar": None},
        ])
        wm = loader.get_watermarks(engine=engine, now=fixed_today)
        assert wm.mei_pack_yyyymm == "202605"

    def test_default_now_is_real_today_when_not_injected(self):
        """Sanity check that the `now` param is truly optional (production
        call site never passes it) and defaults to the real date.today()."""
        far_future = date(date.today().year + 5, 1, 1)
        engine, _conn = _mock_engine([
            {"scalar": far_future},
            {"scalar": None},
            {"scalar": None},
        ])
        wm = loader.get_watermarks(engine=engine)  # no `now` -- real date.today()
        assert wm.ccpi_published_at == date.today()


class TestPerArtifactBatchingShape:
    """The core memory-bound fix: each artifact must be parsed AND upserted
    immediately, one at a time -- never accumulated into one list first. Uses
    call-order tracking (not just "no exception") so the test has teeth
    against a regression back to the accumulate-everything shape."""

    def _write_dummy_ccpi_cache(self, cache_dir: Path, dates: list) -> None:
        cache_dir.mkdir(parents=True, exist_ok=True)
        for d in dates:
            (cache_dir / f"cbsl_ccpi_{d.isoformat().replace('-', '')}.pdf").write_bytes(b"%PDF-1.4 fake")

    def test_ccpi_upsert_called_immediately_after_each_parse_not_accumulated(
        self, tmp_path, monkeypatch
    ):
        cache_dir = tmp_path / "cbsl_cache"
        dates = [date(2026, 1, 31), date(2026, 2, 28), date(2026, 3, 31)]
        self._write_dummy_ccpi_cache(cache_dir, dates)

        call_log: list = []

        def fake_parse(pdf_path, *, filename_pub_date=None):
            call_log.append(("parse", filename_pub_date))
            return [parser.ParsedMacroPoint(
                series_code=parser.SERIES_CCPI_INDEX,
                reference_date=filename_pub_date.replace(day=1),
                value=100.0, source="CBSL_CCPI", filename_pub_date=filename_pub_date,
            )]

        def fake_upsert(points, *, dry_run=False):
            assert len(points) == 1, (
                "an accumulate-then-upsert-once regression would eventually "
                "call this with points from MULTIPLE artifacts at once -- "
                "each call here must see exactly ONE artifact's points"
            )
            call_log.append(("upsert", points[0].filename_pub_date))
            return {"inserted": 1, "updated": 0, "skipped_invalid": 0}

        monkeypatch.setattr(parser, "parse_ccpi_pdf", fake_parse)
        monkeypatch.setattr(loader, "upsert_macro_points", fake_upsert)
        monkeypatch.setattr(loader, "get_watermarks", lambda *a, **k: loader.MacroWatermarks(None, None))

        summary = ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True, dry_run=False)

        expected = [
            ("parse", dates[0]), ("upsert", dates[0]),
            ("parse", dates[1]), ("upsert", dates[1]),
            ("parse", dates[2]), ("upsert", dates[2]),
        ]
        assert call_log == expected, (
            f"expected strict parse/upsert interleaving, got {call_log} -- an "
            "accumulate-then-upsert-once shape would show all 3 parses "
            "before any upsert"
        )
        assert loader.upsert_macro_points is fake_upsert  # sanity: still patched
        assert summary["artifactsFetched"] == 3
        assert summary["rowsInserted"] == 3
        assert summary["artifactsWatermarkSkipped"] == 0

    def test_parse_failure_on_one_artifact_does_not_abort_the_rest(self, tmp_path, monkeypatch):
        """Existing skip-accounting contract, extended: a 0-point parse for
        ONE artifact must not stop the other artifacts from being processed
        (item 3 of the fix contract)."""
        cache_dir = tmp_path / "cbsl_cache"
        dates = [date(2026, 1, 31), date(2026, 2, 28), date(2026, 3, 31)]
        self._write_dummy_ccpi_cache(cache_dir, dates)

        def fake_parse(pdf_path, *, filename_pub_date=None):
            if filename_pub_date == date(2026, 2, 28):
                return []  # simulated parse failure for the middle artifact
            return [parser.ParsedMacroPoint(
                series_code=parser.SERIES_CCPI_INDEX,
                reference_date=filename_pub_date.replace(day=1),
                value=1.0, source="CBSL_CCPI", filename_pub_date=filename_pub_date,
            )]

        def fake_upsert(points, *, dry_run=False):
            return {"inserted": len(points), "updated": 0, "skipped_invalid": 0}

        monkeypatch.setattr(parser, "parse_ccpi_pdf", fake_parse)
        monkeypatch.setattr(loader, "upsert_macro_points", fake_upsert)
        monkeypatch.setattr(loader, "get_watermarks", lambda *a, **k: loader.MacroWatermarks(None, None))

        summary = ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True, dry_run=False)

        assert summary["artifactsFetched"] == 2
        assert summary["artifactsSkipped"] == 1
        assert summary["rowsInserted"] == 2

    def test_upsert_exception_on_one_artifact_does_not_abort_the_rest(self, tmp_path, monkeypatch):
        """Per-artifact DB-write isolation: the OLD single-big-transaction
        shape would have lost every artifact's rows if any one upsert raised.
        The new per-artifact shape must only lose the ONE bad artifact."""
        cache_dir = tmp_path / "cbsl_cache"
        dates = [date(2026, 1, 31), date(2026, 2, 28), date(2026, 3, 31)]
        self._write_dummy_ccpi_cache(cache_dir, dates)

        def fake_parse(pdf_path, *, filename_pub_date=None):
            return [parser.ParsedMacroPoint(
                series_code=parser.SERIES_CCPI_INDEX,
                reference_date=filename_pub_date.replace(day=1),
                value=1.0, source="CBSL_CCPI", filename_pub_date=filename_pub_date,
            )]

        def fake_upsert(points, *, dry_run=False):
            if points[0].filename_pub_date == date(2026, 2, 28):
                raise RuntimeError("simulated DB blip")
            return {"inserted": len(points), "updated": 0, "skipped_invalid": 0}

        monkeypatch.setattr(parser, "parse_ccpi_pdf", fake_parse)
        monkeypatch.setattr(loader, "upsert_macro_points", fake_upsert)
        monkeypatch.setattr(loader, "get_watermarks", lambda *a, **k: loader.MacroWatermarks(None, None))

        summary = ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True, dry_run=False)

        assert summary["artifactsFetched"] == 2, "the two GOOD artifacts must still land"
        assert summary["artifactsSkipped"] == 0, (
            "a DB upsert exception is a FAILURE, not a '0 series parsed' "
            "skip -- it must never be folded into artifactsSkipped"
        )
        assert summary["artifactsFailed"] == 1, "the ONE bad artifact must be counted as a genuine failure"
        assert summary["rowsInserted"] == 2
        assert summary["status"] == "partial", (
            "status must NOT be 'ok' when a DB write failed -- that was the "
            "honest-reporting regression this fixes (old code: status='ok' / "
            "exit 0 even though an artifact's rows were silently lost)"
        )


class TestFailureReporting:
    """BLOCKER 2 (2026-08-17 review): a DB-write failure used to report
    status='ok' + exit 0, identical to a legitimate zero-new-artifacts pass
    -- a real regression vs. main(), where the exception used to propagate
    loudly. artifactsFailed/status='partial' plus a non-zero CLI exit code
    fix this without reverting the per-artifact isolation itself."""

    def _run_with_one_failure(self, tmp_path, monkeypatch):
        cache_dir = tmp_path / "cbsl_cache"
        cache_dir.mkdir(parents=True, exist_ok=True)
        (cache_dir / "cbsl_ccpi_20260228.pdf").write_bytes(b"%PDF-1.4 fake")

        def fake_parse(pdf_path, *, filename_pub_date=None):
            return [parser.ParsedMacroPoint(
                series_code=parser.SERIES_CCPI_INDEX,
                reference_date=filename_pub_date.replace(day=1),
                value=1.0, source="CBSL_CCPI", filename_pub_date=filename_pub_date,
            )]

        def fake_upsert(points, *, dry_run=False):
            raise RuntimeError("simulated DB blip")

        monkeypatch.setattr(parser, "parse_ccpi_pdf", fake_parse)
        monkeypatch.setattr(loader, "upsert_macro_points", fake_upsert)
        monkeypatch.setattr(loader, "get_watermarks", lambda *a, **k: loader.MacroWatermarks(None, None))
        return ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True)

    def test_zero_new_artifacts_keeps_status_ok_and_zero_failed(self, tmp_path, monkeypatch):
        """'No new bulletin since watermark' must stay untouched by this fix."""
        cache_dir = tmp_path / "cbsl_cache"
        cache_dir.mkdir(parents=True, exist_ok=True)
        monkeypatch.setattr(loader, "get_watermarks", lambda *a, **k: loader.MacroWatermarks(None, None))

        summary = ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True)

        assert summary["status"] == "ok"
        assert summary["artifactsFailed"] == 0

    def test_upsert_failure_flips_status_to_partial(self, tmp_path, monkeypatch):
        summary = self._run_with_one_failure(tmp_path, monkeypatch)
        assert summary["status"] == "partial"
        assert summary["artifactsFailed"] == 1

    def test_cli_main_exits_zero_on_ok_status(self, monkeypatch):
        ok_summary = {
            "status": "ok", "artifactsFetched": 0, "artifactsSkipped": 0,
            "artifactsFailed": 0, "artifactsWatermarkSkipped": 0,
            "rowsInserted": 0, "rowsUpdated": 0, "rowsSkippedInvalid": 0,
            "perSeriesCoverage": {},
        }
        monkeypatch.setattr(ingest_cbsl_macro, "run", lambda **kw: ok_summary)
        monkeypatch.setattr(sys, "argv", ["ingest_cbsl_macro.py"])

        with pytest.raises(SystemExit) as exc_info:
            ingest_cbsl_macro.main()
        assert exc_info.value.code == 0, "'no new bulletin' (status=ok) must still exit 0"

    def test_cli_main_exits_nonzero_on_partial_status(self, monkeypatch):
        partial_summary = {
            "status": "partial", "artifactsFetched": 2, "artifactsSkipped": 0,
            "artifactsFailed": 1, "artifactsWatermarkSkipped": 0,
            "rowsInserted": 2, "rowsUpdated": 0, "rowsSkippedInvalid": 0,
            "perSeriesCoverage": {"CCPI_BASE2021": 2},
        }
        monkeypatch.setattr(ingest_cbsl_macro, "run", lambda **kw: partial_summary)
        monkeypatch.setattr(sys, "argv", ["ingest_cbsl_macro.py"])

        with pytest.raises(SystemExit) as exc_info:
            ingest_cbsl_macro.main()
        assert exc_info.value.code != 0, (
            "a DB-write failure must exit non-zero so k8s marks the Job "
            "Failed -- silently exiting 0 here was the exact regression"
        )
        assert exc_info.value.code == 1


class TestFullBackfillEmptyDb:
    """A true full backfill (empty DB) must process the WHOLE cache with zero
    watermark skips -- the watermark logic must never accidentally throttle
    the very case it is designed to leave untouched."""

    def test_empty_db_watermark_processes_every_cached_artifact(self, tmp_path, monkeypatch):
        cache_dir = tmp_path / "cbsl_cache"
        cache_dir.mkdir(parents=True, exist_ok=True)
        dates = [date(2020, 4, 30), date(2023, 1, 31), date(2026, 6, 30)]
        for d in dates:
            (cache_dir / f"cbsl_ccpi_{d.isoformat().replace('-', '')}.pdf").write_bytes(b"%PDF-1.4 fake")

        def fake_parse(pdf_path, *, filename_pub_date=None):
            return [parser.ParsedMacroPoint(
                series_code=parser.SERIES_CCPI_INDEX,
                reference_date=filename_pub_date.replace(day=1),
                value=1.0, source="CBSL_CCPI", filename_pub_date=filename_pub_date,
            )]

        def fake_upsert(points, *, dry_run=False):
            return {"inserted": len(points), "updated": 0, "skipped_invalid": 0}

        monkeypatch.setattr(parser, "parse_ccpi_pdf", fake_parse)
        monkeypatch.setattr(loader, "upsert_macro_points", fake_upsert)
        # Empty DB: get_watermarks() would genuinely return (None, None) here --
        # simulated directly rather than hitting a real DB (hermetic test).
        monkeypatch.setattr(loader, "get_watermarks", lambda *a, **k: loader.MacroWatermarks(None, None))

        summary = ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True, dry_run=False)

        assert summary["artifactsWatermarkSkipped"] == 0
        assert summary["artifactsFetched"] == len(dates) == 3
        assert summary["rowsInserted"] == 3


class TestFullFlagOverride:
    def test_full_flag_never_calls_get_watermarks(self, tmp_path, monkeypatch):
        cache_dir = tmp_path / "cbsl_cache"
        cache_dir.mkdir(parents=True, exist_ok=True)

        watermark_mock = MagicMock(side_effect=AssertionError("get_watermarks must not be called under --full"))
        monkeypatch.setattr(loader, "get_watermarks", watermark_mock)

        summary = ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True, full=True)

        assert watermark_mock.call_count == 0
        assert summary["status"] == "ok"

    def test_full_flag_processes_artifacts_the_watermark_would_have_dropped(self, tmp_path, monkeypatch):
        cache_dir = tmp_path / "cbsl_cache"
        cache_dir.mkdir(parents=True, exist_ok=True)
        old_date = date(2020, 4, 30)
        (cache_dir / f"cbsl_ccpi_{old_date.isoformat().replace('-', '')}.pdf").write_bytes(b"%PDF-1.4 fake")

        def fake_parse(pdf_path, *, filename_pub_date=None):
            return [parser.ParsedMacroPoint(
                series_code=parser.SERIES_CCPI_INDEX,
                reference_date=filename_pub_date.replace(day=1),
                value=1.0, source="CBSL_CCPI", filename_pub_date=filename_pub_date,
            )]

        def fake_upsert(points, *, dry_run=False):
            return {"inserted": len(points), "updated": 0, "skipped_invalid": 0}

        monkeypatch.setattr(parser, "parse_ccpi_pdf", fake_parse)
        monkeypatch.setattr(loader, "upsert_macro_points", fake_upsert)
        # A DB watermark far in the future -- would drop the old_date artifact
        # entirely if respected.
        far_future_watermarks = loader.MacroWatermarks(ccpi_published_at=date(2026, 6, 30), mei_pack_yyyymm="202605")
        monkeypatch.setattr(loader, "get_watermarks", lambda *a, **k: far_future_watermarks)

        # Without --full: dropped by the watermark.
        summary_incremental = ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True, full=False)
        assert summary_incremental["artifactsWatermarkSkipped"] == 1
        assert summary_incremental["artifactsFetched"] == 0

        # With --full: watermark bypassed entirely, the artifact IS processed.
        summary_full = ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True, full=True)
        assert summary_full["artifactsWatermarkSkipped"] == 0
        assert summary_full["artifactsFetched"] == 1
        assert summary_full["rowsInserted"] == 1


class TestSummaryContractPreserved:
    """The summary dict shape mirrors ingest_harti_service.run()'s contract
    and is consumed by the FastAPI admin route -- these keys must always be
    present, regardless of the watermark/batching internals."""

    REQUIRED_KEYS = {
        "status", "artifactsFetched", "artifactsSkipped",
        "rowsInserted", "rowsUpdated", "rowsSkippedInvalid",
        "perSeriesCoverage",
    }

    def test_zero_new_artifacts_is_a_success_with_the_full_key_set(self, tmp_path, monkeypatch):
        cache_dir = tmp_path / "cbsl_cache"
        cache_dir.mkdir(parents=True, exist_ok=True)  # completely empty cache
        monkeypatch.setattr(loader, "get_watermarks", lambda *a, **k: loader.MacroWatermarks(None, None))

        summary = ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True)

        assert self.REQUIRED_KEYS <= set(summary.keys())
        assert summary["status"] == "ok"
        assert summary["artifactsFetched"] == 0
        assert summary["artifactsSkipped"] == 0
        assert summary["rowsInserted"] == 0
        assert summary["rowsUpdated"] == 0
        assert summary["rowsSkippedInvalid"] == 0
        assert summary["perSeriesCoverage"] == {}
        assert isinstance(summary["perSeriesCoverage"], dict)

    def test_watermark_skip_field_is_additive_not_a_contract_break(self, tmp_path, monkeypatch):
        """artifactsWatermarkSkipped is a NEW field -- must never replace or
        rename any of the required keys."""
        cache_dir = tmp_path / "cbsl_cache"
        cache_dir.mkdir(parents=True, exist_ok=True)
        monkeypatch.setattr(loader, "get_watermarks", lambda *a, **k: loader.MacroWatermarks(None, None))

        summary = ingest_cbsl_macro.run(cache_dir=cache_dir, no_download=True)

        assert self.REQUIRED_KEYS <= set(summary.keys())
        assert "artifactsWatermarkSkipped" in summary
        assert summary["artifactsWatermarkSkipped"] == 0
        assert "artifactsFailed" in summary, "additive field -- must never replace an existing key"
        assert summary["artifactsFailed"] == 0


class TestRetrievedAtUtcInvariant:
    """MAJOR 4 (2026-08-17 review): cross-branch invariant lock-in. The
    sibling .NET staleness alert (PipelineSentinel / feat/macro-staleness-
    alert, an in-flight parallel branch touching the same DB) depends on
    RetrievedAtUtc advancing on EVERY re-upsert of an already-existing row,
    even when Value is byte-for-byte unchanged -- that is its only signal
    that a pass actually ran and touched the row. Deleting `RetrievedAtUtc =
    :retrieved_at` from the UPDATE clause (loader.py:384) survives every
    OTHER test in this suite; this test exists specifically to catch that,
    and an "only update if Value changed" guard as well (both would skip
    setting a fresh RetrievedAtUtc on an unchanged row)."""

    @staticmethod
    def _engine_reporting_one_existing_key(capture: list):
        engine = MagicMock()
        conn = MagicMock()

        def _execute(stmt, params=None):
            stmt_text = str(stmt)
            capture.append((stmt_text, params))
            res = MagicMock()
            if stmt_text.strip().upper().startswith("SELECT SERIESCODE"):
                res.fetchall.return_value = [("CCPI_BASE2021", "2026-06-01", "2026-06-30")]
            return res

        conn.execute.side_effect = _execute
        engine.begin.return_value.__enter__.return_value = conn
        return engine

    def test_update_path_bumps_retrieved_at_on_an_unchanged_re_upserted_row(self):
        capture: list = []
        engine = self._engine_reporting_one_existing_key(capture)

        # Same key AND same value as the mocked "existing" row -- a
        # completely unchanged re-upsert of an already-ingested artifact,
        # exactly what the 1-month watermark overlap causes routinely.
        p = parser.ParsedMacroPoint(
            series_code="CCPI_BASE2021", reference_date=date(2026, 6, 1), value=207.7,
            source="CBSL_CCPI", filename_pub_date=date(2026, 6, 30),
        )
        counters = loader.upsert_macro_points([p], engine=engine, dry_run=False)

        assert counters["updated"] == 1, "must take the UPDATE path for an existing key"

        update_calls = [
            (stmt, params) for stmt, params in capture
            if stmt.strip().upper().startswith("UPDATE")
        ]
        assert len(update_calls) == 1, "exactly one UPDATE call for the one row, whether or not Value changed"

        update_stmt, update_params = update_calls[0]
        assert "RetrievedAtUtc" in update_stmt, (
            "the UPDATE statement must SET RetrievedAtUtc -- this is the "
            "ONLY signal the sibling .NET staleness alert has that a pass "
            "actually touched this row"
        )
        assert update_params.get("retrieved_at") is not None, (
            "RetrievedAtUtc must be bound to a real value, never omitted/None"
        )
        assert isinstance(update_params["retrieved_at"], datetime), (
            "must be a real datetime -- a None or placeholder here would "
            "silently stop the staleness alert from ever clearing"
        )


class TestWatermarkOverlapConstantPin:
    """NIT 8 (2026-08-17 review): downloader.py's module-scope comment for
    _WATERMARK_OVERLAP_MONTHS claims a test pins it -- this is that test."""

    def test_overlap_constant_is_one_month(self):
        assert downloader._WATERMARK_OVERLAP_MONTHS == 1

    def test_filter_functions_default_to_the_module_constant_not_a_hardcoded_literal(self):
        """Proves filter_ccpi_since/filter_mei_since's default `overlap_months`
        actually REFERENCES _WATERMARK_OVERLAP_MONTHS rather than
        independently hardcoding the literal 1 -- changing the constant
        would change the defaults too."""
        import inspect
        ccpi_default = inspect.signature(downloader.filter_ccpi_since).parameters["overlap_months"].default
        mei_default = inspect.signature(downloader.filter_mei_since).parameters["overlap_months"].default
        assert ccpi_default == downloader._WATERMARK_OVERLAP_MONTHS
        assert mei_default == downloader._WATERMARK_OVERLAP_MONTHS
