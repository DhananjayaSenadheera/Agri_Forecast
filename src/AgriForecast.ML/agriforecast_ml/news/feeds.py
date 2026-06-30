"""Curated list of free English RSS feeds for AgriForecast news ingestion.

Selection criteria:
  - Freely available RSS (no API key, no login).
  - English language.
  - Relevant to: Sri Lankan agriculture, commodity/food prices, weather disasters,
    regional trade, or global crop-market signals that affect SL prices.

Feeds are grouped by category for clarity.  The fetcher tolerates dead feeds
gracefully -- if a feed URL becomes stale it warns and continues; it does NOT
abort the run.

Column produced by the fetcher: Source (str) maps to the 'source' field here.
"""
from __future__ import annotations

from typing import TypedDict


class FeedSpec(TypedDict):
    source: str       # short identifier stored in NewsArticles.Source
    url: str          # RSS URL
    category: str     # informational label for logging/reporting


# ---------------------------------------------------------------------------
# SL English-language news outlets -- agriculture / business / economy sections
# ---------------------------------------------------------------------------
_SL_FEEDS: list[FeedSpec] = [
    {
        "source": "economy_next",
        "url": "https://economynext.com/feed/",
        "category": "SL_economy",
    },
    {
        "source": "daily_ft",
        "url": "https://www.ft.lk/rss/",
        "category": "SL_economy",
    },
    {
        "source": "lbo",
        "url": "https://www.lankabusinessonline.com/feed/",
        "category": "SL_economy",
    },
    {
        "source": "news_first",
        "url": "https://newsfirst.lk/feed/",
        "category": "SL_news",
    },
    {
        "source": "daily_mirror_lk",
        "url": "https://www.dailymirror.lk/rss.xml",
        "category": "SL_news",
    },
    {
        "source": "ada_derana_biz",
        "url": "https://bizenglish.adaderana.lk/feed/",
        "category": "SL_economy",
    },
    {
        "source": "island_lk",
        "url": "https://island.lk/feed/",
        "category": "SL_news",
    },
]

# ---------------------------------------------------------------------------
# Global / regional agriculture & commodity feeds
# ---------------------------------------------------------------------------
_GLOBAL_AG_FEEDS: list[FeedSpec] = [
    {
        "source": "fao_news",
        "url": "https://www.fao.org/feeds/rss/en/news/",
        "category": "global_ag",
    },
    {
        "source": "reuters_biz",
        "url": "https://feeds.reuters.com/reuters/businessNews",
        "category": "global_commodity",
    },
    {
        "source": "reliefweb_lka",
        "url": "https://reliefweb.int/country/lka/rss.xml",
        "category": "SL_disaster_weather",
    },
    {
        "source": "agrifarming_in",
        "url": "https://www.agrifarming.in/feed",
        "category": "regional_ag",
    },
    {
        "source": "worldbank_food",
        "url": "https://feeds.worldbank.org/wb/news/food",
        "category": "global_commodity",
    },
]

# ---------------------------------------------------------------------------
# Public export
# ---------------------------------------------------------------------------
FEEDS: list[FeedSpec] = _SL_FEEDS + _GLOBAL_AG_FEEDS


def feeds_by_category() -> dict[str, list[FeedSpec]]:
    """Return feeds grouped by category (for reporting / diagnostics)."""
    result: dict[str, list[FeedSpec]] = {}
    for f in FEEDS:
        result.setdefault(f["category"], []).append(f)
    return result
