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
