"""
AgriForecast ML -- News ingestion tests (Phase 5 Chunk C1).

Coverage (all fast, no live network, no live DB):
  TestFeedSpec          -- feeds.FEEDS is non-empty, keys present, sources unique.
  TestParsing           -- feedparser parses fixture RSS: title, URL, pub date.
  TestLanguageFilter    -- only English entries pass the language gate.
  TestSummaryTruncation -- summary capped at SUMMARY_MAX_CHARS chars, HTML stripped.
  TestDedup             -- idempotent reload inserts 0 new rows (dedup on Url).
  TestUpsertCounting    -- inserted + dup_skipped == total; empty input returns zeros.
  TestEnsureTable       -- ensure_table DDL runs without error on a mock engine.
"""
from __future__ import annotations

import sys
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

# Path setup
ML_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ML_ROOT))

FIXTURE_FEED = Path(__file__).parent / "fixtures" / "sample_feed.xml"


# Helpers

def _parsed_feed():
    """Return feedparser result from the local fixture (no network)."""
    import feedparser
    with open(FIXTURE_FEED, "rb") as f:
        content = f.read()
    return feedparser.parse(content)


# TestFeedSpec

class TestFeedSpec:
    """feeds.FEEDS is non-empty and each entry has required keys."""

    def test_feeds_non_empty(self):
        from agriforecast_ml.news.feeds import FEEDS
        assert len(FEEDS) >= 5, "Expected at least 5 feeds"

    def test_each_feed_has_required_keys(self):
        from agriforecast_ml.news.feeds import FEEDS
        required = {"source", "url", "category"}
        for feed in FEEDS:
            missing = required - set(feed.keys())
            assert not missing, f"Feed {feed} missing keys: {missing}"

    def test_sources_are_unique(self):
        from agriforecast_ml.news.feeds import FEEDS
        sources = [f["source"] for f in FEEDS]
        assert len(sources) == len(set(sources)), "Feed sources must be unique"

    def test_urls_start_with_http(self):
        from agriforecast_ml.news.feeds import FEEDS
        for feed in FEEDS:
            assert feed["url"].startswith("http"), (
                f"Feed {feed['source']} URL does not start with http: {feed['url']}"
            )


# TestParsing

class TestParsing:
    """feedparser correctly parses the local fixture RSS."""

    def test_fixture_file_exists(self):
        assert FIXTURE_FEED.exists(), f"Fixture not found: {FIXTURE_FEED}"

    def test_fixture_has_five_entries(self):
        parsed = _parsed_feed()
        assert len(parsed.entries) == 5

    def test_first_entry_title_contains_dambulla(self):
        parsed = _parsed_feed()
        title = parsed.entries[0].get("title", "")
        assert "Dambulla" in title or "vegetable" in title.lower()

    def test_first_entry_url(self):
        parsed = _parsed_feed()
        assert parsed.entries[0].get("link") == "https://example-agri.test/story/1"

    def test_pub_date_parsed_to_aware_datetime(self):
        from agriforecast_ml.news.fetcher import _parse_published
        parsed = _parsed_feed()
        dt = _parse_published(parsed.entries[0])
        assert dt is not None, "Expected parsed datetime for story/1"
        assert dt.tzinfo is not None, "datetime must be timezone-aware"
        assert dt.year == 2025


# TestLanguageFilter

class TestLanguageFilter:
    """Only English entries pass the language gate."""

    def test_sinhala_entry_rejected(self):
        from agriforecast_ml.news.fetcher import _is_english
        parsed = _parsed_feed()
        # Entry index 3 (story/4) has <language>si</language>
        assert not _is_english(parsed.entries[3], parsed), (
            "Sinhala-tagged entry must be rejected"
        )

    def test_unlabelled_english_entries_pass(self):
        from agriforecast_ml.news.fetcher import _is_english
        parsed = _parsed_feed()
        for i, entry in enumerate(parsed.entries):
            if i == 3:
                continue   # sinhala -- expected to fail
            assert _is_english(entry, parsed), f"Entry {i} should pass English filter"

    def test_fetch_feed_excludes_sinhala_entry(self):
        """fetch_feed must exclude the si-tagged entry (story/4)."""
        from agriforecast_ml.news import fetcher
        import feedparser

        feed_spec = {"source": "fixture", "url": "https://economynext.com/feed/", "category": "test"}
        with open(FIXTURE_FEED, "rb") as f:
            raw = f.read()

        with patch("feedparser.parse", return_value=feedparser.parse(raw)):
            articles = fetcher.fetch_feed(feed_spec)

        urls = [a["url"] for a in articles]
        assert "https://example-agri.test/story/4" not in urls
        assert len(articles) == 4, f"Expected 4 English articles, got {len(articles)}"


# TestSummaryTruncation

class TestSummaryTruncation:
    """Summary is truncated to SUMMARY_MAX_CHARS; HTML is stripped first."""

    def test_long_text_truncated_to_limit(self):
        from agriforecast_ml.news.fetcher import _truncate, SUMMARY_MAX_CHARS
        result = _truncate("B" * 2000, SUMMARY_MAX_CHARS)
        assert len(result) == SUMMARY_MAX_CHARS

    def test_short_text_unchanged(self):
        from agriforecast_ml.news.fetcher import _truncate, SUMMARY_MAX_CHARS
        short = "Short summary."
        assert _truncate(short, SUMMARY_MAX_CHARS) == short

    def test_html_tags_stripped(self):
        from agriforecast_ml.news.fetcher import _truncate
        html = "<p>Hello <strong>world</strong></p>"
        result = _truncate(html, 500)
        assert "<" not in result
        assert "Hello" in result
        assert "world" in result

    def test_story5_summary_within_limit(self):
        """story/5 in the fixture has a 1500-char body; must be capped at 1000."""
        from agriforecast_ml.news import fetcher
        import feedparser

        feed_spec = {"source": "fixture", "url": "https://economynext.com/feed/", "category": "test"}
        with open(FIXTURE_FEED, "rb") as f:
            raw = f.read()

        with patch("feedparser.parse", return_value=feedparser.parse(raw)):
            articles = fetcher.fetch_feed(feed_spec)

        story5 = next(
            (a for a in articles if a["url"] == "https://example-agri.test/story/5"),
            None,
        )
        assert story5 is not None, "story/5 must be in fetched articles"
        assert len(story5["summary"]) <= fetcher.SUMMARY_MAX_CHARS


# TestDedup

class TestDedup:
    """Idempotent reload: second run inserts 0 new rows."""

    def _articles(self, n=3):
        from agriforecast_ml.news.fetcher import RawArticle
        now = datetime.now(timezone.utc)
        return [
            RawArticle(
                source="test", url=f"https://x.test/{i}",
                title=f"T{i}", summary=f"S{i}",
                published_utc=now, retrieved_utc=now, language="en",
            )
            for i in range(n)
        ]

    def _mock_engine(self, existing_urls):
        mock_eng = MagicMock()
        mock_conn = MagicMock()
        mock_eng.begin.return_value.__enter__ = MagicMock(return_value=mock_conn)
        mock_eng.begin.return_value.__exit__ = MagicMock(return_value=False)
        mock_result = MagicMock()
        mock_result.fetchall.return_value = [(url,) for url in existing_urls]
        mock_conn.execute.return_value = mock_result
        return mock_eng, mock_conn

    def test_first_load_inserts_all(self):
        from agriforecast_ml.news import loader
        articles = self._articles(3)
        mock_eng, _ = self._mock_engine(set())
        with patch.object(loader, "ensure_table"):
            counters = loader.upsert_articles(articles, engine=mock_eng)
        assert counters["inserted"] == 3
        assert counters["dup_skipped"] == 0

    def test_second_load_inserts_zero(self):
        from agriforecast_ml.news import loader
        articles = self._articles(3)
        existing = {a["url"] for a in articles}
        mock_eng, _ = self._mock_engine(existing)
        with patch.object(loader, "ensure_table"):
            counters = loader.upsert_articles(articles, engine=mock_eng)
        assert counters["inserted"] == 0
        assert counters["dup_skipped"] == 3

    def test_partial_dedup(self):
        from agriforecast_ml.news import loader
        articles = self._articles(4)
        existing = {articles[0]["url"], articles[1]["url"]}
        mock_eng, _ = self._mock_engine(existing)
        with patch.object(loader, "ensure_table"):
            counters = loader.upsert_articles(articles, engine=mock_eng)
        assert counters["inserted"] == 2
        assert counters["dup_skipped"] == 2


# TestUpsertCounting

class TestUpsertCounting:
    """inserted + dup_skipped == total, always."""

    def test_counts_sum_to_total(self):
        from agriforecast_ml.news import loader
        from agriforecast_ml.news.fetcher import RawArticle

        now = datetime.now(timezone.utc)
        articles = [
            RawArticle(
                source="t", url=f"https://x.test/{i}",
                title=f"T{i}", summary=f"S{i}",
                published_utc=now, retrieved_utc=now, language="en",
            )
            for i in range(5)
        ]
        existing = {articles[0]["url"], articles[2]["url"]}

        mock_eng = MagicMock()
        mock_conn = MagicMock()
        mock_eng.begin.return_value.__enter__ = MagicMock(return_value=mock_conn)
        mock_eng.begin.return_value.__exit__ = MagicMock(return_value=False)
        mock_result = MagicMock()
        mock_result.fetchall.return_value = [(url,) for url in existing]
        mock_conn.execute.return_value = mock_result

        with patch.object(loader, "ensure_table"):
            counters = loader.upsert_articles(articles, engine=mock_eng)

        assert counters["inserted"] + counters["dup_skipped"] == counters["total"]
        assert counters["total"] == 5
        assert counters["inserted"] == 3
        assert counters["dup_skipped"] == 2

    def test_empty_input_returns_zeros(self):
        from agriforecast_ml.news import loader
        counters = loader.upsert_articles([], engine=MagicMock())
        assert counters == {"inserted": 0, "dup_skipped": 0, "total": 0}


# TestEnsureTable

class TestEnsureTable:
    """ensure_table executes the DDL against the engine without errors."""

    def test_ensure_table_calls_execute(self):
        from agriforecast_ml.news import loader
        mock_eng = MagicMock()
        mock_conn = MagicMock()
        mock_eng.begin.return_value.__enter__ = MagicMock(return_value=mock_conn)
        mock_eng.begin.return_value.__exit__ = MagicMock(return_value=False)
        loader.ensure_table(engine=mock_eng)
        mock_conn.execute.assert_called_once()

    def test_ensure_table_idempotent_on_mock(self):
        """Calling ensure_table twice raises no exception."""
        from agriforecast_ml.news import loader
        mock_eng = MagicMock()
        mock_conn = MagicMock()
        mock_eng.begin.return_value.__enter__ = MagicMock(return_value=mock_conn)
        mock_eng.begin.return_value.__exit__ = MagicMock(return_value=False)
        loader.ensure_table(engine=mock_eng)
        loader.ensure_table(engine=mock_eng)
        assert mock_conn.execute.call_count == 2
