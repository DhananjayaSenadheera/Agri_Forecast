"""Fetch and parse RSS feeds with feedparser.

Design:
  - Per-feed try/except: a dead or malformed feed logs a warning and is skipped.
    The run never aborts because of one bad feed.
  - Timeouts enforced via socket.setdefaulttimeout (feedparser has no native
    timeout parameter in all versions).
  - Language filter: keeps entries with lang='en' or no lang tag (most SL
    English outlets do not tag entries at entry level; the feed itself is English).
  - Returns a list of RawArticle dicts ready for the loader.
  - Duplicate URL dedup within a single fetch run (before DB upsert) to handle
    feeds that repeat items across pages.

Callers:
  ingest_news.py  -- main entry point.
  tests/test_news_ingestion.py  -- uses a local fixture instead of live network.
"""
from __future__ import annotations

import logging
import re
import socket
from datetime import datetime, timezone
from email.utils import parsedate_to_datetime
from typing import TypedDict

import feedparser

from .feeds import FEEDS, FeedSpec
from ..netguard import DisallowedUrlError, assert_host_allowed

logger = logging.getLogger(__name__)

SUMMARY_MAX_CHARS = 1000   # truncation limit stored in NewsArticles.Summary
TITLE_MAX_CHARS = 500
FETCH_TIMEOUT_SECONDS = 15


class RawArticle(TypedDict):
    source: str
    url: str
    title: str
    summary: str             # already truncated to SUMMARY_MAX_CHARS
    published_utc: "datetime | None"
    retrieved_utc: datetime
    language: str            # always 'en' for items that pass our filter


def _truncate(text: str, max_chars: int = SUMMARY_MAX_CHARS) -> str:
    """Strip HTML tags, collapse whitespace, and truncate to max_chars."""
    # Remove HTML tags
    text = re.sub(r"<[^>]+>", " ", text)
    # Collapse whitespace
    text = re.sub(r"\s+", " ", text).strip()
    return text[:max_chars]


def _parse_published(entry) -> "datetime | None":
    """Extract published datetime as UTC-aware datetime, or None if unparseable."""
    # feedparser populates published_parsed (UTC struct_time) when it can
    if hasattr(entry, "published_parsed") and entry.published_parsed:
        try:
            import calendar
            ts = calendar.timegm(entry.published_parsed)
            return datetime.fromtimestamp(ts, tz=timezone.utc)
        except Exception:
            pass
    # Fallback: raw published string via email.utils
    raw = getattr(entry, "published", None)
    if raw:
        try:
            dt = parsedate_to_datetime(raw)
            return dt.astimezone(timezone.utc)
        except Exception:
            pass
    return None


def _is_english(entry, feed_parsed) -> bool:
    """Return True if the entry/feed appears to be English.

    Most SL English outlets do not tag entries with lang.  We default to True
    (assume English) unless an explicit non-English language tag is present.
    """
    # Entry-level language takes priority
    entry_lang = getattr(entry, "language", None)
    if not entry_lang:
        # Feed-level language
        feed_lang = getattr(feed_parsed.feed, "language", None)
        lang = (feed_lang or "en").lower()
    else:
        lang = entry_lang.lower()
    return lang.startswith("en")


def fetch_feed(
    feed_spec: FeedSpec,
    timeout: int = FETCH_TIMEOUT_SECONDS,
) -> "list[RawArticle]":
    """Fetch a single RSS feed and return parsed RawArticle list.

    Raises nothing -- all exceptions are caught, logged as warnings, and the
    function returns an empty list so the caller can continue with other feeds.
    """
    source = feed_spec["source"]
    url = feed_spec["url"]
    now_utc = datetime.now(timezone.utc)

    # SSRF guard (S1): feedparser fetches the URL internally (and follows
    # redirects) with no host restriction. Validate the feed URL's scheme + host
    # against the ingestion allowlist before handing it to feedparser, so a
    # stale/poisoned feed entry can't point the fetch at an off-allowlist host.
    # (Host-only check — feedparser owns the socket, so we cannot re-validate its
    # redirect chain; the allowlist on the configured URL is the enforceable
    # control here.) A disallowed URL is skipped like any other bad feed.
    try:
        assert_host_allowed(url)
    except DisallowedUrlError as exc:
        logger.warning("[%s] Feed URL blocked by SSRF allowlist: %s", source, exc)
        return []

    try:
        old_timeout = socket.getdefaulttimeout()
        socket.setdefaulttimeout(timeout)
        try:
            parsed = feedparser.parse(url)
        finally:
            socket.setdefaulttimeout(old_timeout)

        # feedparser never raises -- check for bozo (malformed/unreachable) feed
        if parsed.bozo and not parsed.entries:
            exc = getattr(parsed, "bozo_exception", None)
            logger.warning(
                "[%s] Bozo feed (malformed / unreachable): %s -- %s",
                source, url, exc,
            )
            return []

        entries = parsed.entries
        if not entries:
            logger.warning("[%s] Feed returned 0 entries: %s", source, url)
            return []

        articles: list[RawArticle] = []
        skipped_lang = 0

        for entry in entries:
            if not _is_english(entry, parsed):
                skipped_lang += 1
                continue

            link = getattr(entry, "link", None) or getattr(entry, "id", None)
            if not link:
                logger.debug("[%s] Entry has no URL -- skipping", source)
                continue

            title_raw = getattr(entry, "title", "") or ""
            summary_raw = (
                getattr(entry, "summary", "")
                or getattr(entry, "description", "")
                or ""
            )

            articles.append(RawArticle(
                source=source,
                url=link.strip(),
                title=_truncate(title_raw, TITLE_MAX_CHARS),
                summary=_truncate(summary_raw, SUMMARY_MAX_CHARS),
                published_utc=_parse_published(entry),
                retrieved_utc=now_utc,
                language="en",
            ))

        logger.info(
            "[%s] Fetched %d entries -> %d articles (skipped %d non-en)",
            source, len(entries), len(articles), skipped_lang,
        )
        return articles

    except Exception as exc:
        logger.warning(
            "[%s] Unexpected error fetching %s: %s: %s",
            source, url, type(exc).__name__, exc,
        )
        return []


def fetch_all(
    feeds: "list[FeedSpec] | None" = None,
    timeout: int = FETCH_TIMEOUT_SECONDS,
) -> "tuple[list[RawArticle], dict[str, int]]":
    """Fetch all feeds and return (deduplicated articles, per-feed entry counts).

    Args:
        feeds:   Feed list; defaults to FEEDS from feeds.py.
        timeout: Per-feed socket timeout in seconds.

    Returns:
        articles: Deduplicated list of RawArticle (URL is the dedup key).
        coverage: {source: n_articles_fetched} for all feeds attempted.
    """
    if feeds is None:
        feeds = FEEDS

    all_articles: list[RawArticle] = []
    coverage: dict[str, int] = {}
    seen_urls: set[str] = set()

    for feed_spec in feeds:
        source = feed_spec["source"]
        articles = fetch_feed(feed_spec, timeout=timeout)
        coverage[source] = len(articles)

        for art in articles:
            if art["url"] not in seen_urls:
                seen_urls.add(art["url"])
                all_articles.append(art)
            # Cross-feed duplicates silently skipped; DB handles cross-run dedup

    logger.info(
        "fetch_all complete: %d feeds attempted, %d unique articles",
        len(feeds), len(all_articles),
    )
    return all_articles, coverage
