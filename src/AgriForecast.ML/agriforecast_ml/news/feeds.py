"""Curated list of free English RSS feeds for news ingestion.

Selection: freely available RSS (no API key or login), English, and relevant to Sri
Lankan agriculture, food prices, weather disasters, regional trade or global crop-market
signals. Feeds are grouped by category, and the fetcher tolerates a dead feed by warning
and carrying on rather than aborting the run.

Several feeds were removed after going dead: Reuters business and World Bank food (hosts
decommissioned), FAO news (404), Daily FT (now JS-gated HTML), and News First and Daily
Mirror (Cloudflare 403 to all bots). The last two are good sources but unreachable
without a headless browser.
"""
from __future__ import annotations

from typing import TypedDict


class FeedSpec(TypedDict):
    source: str       # short identifier stored in NewsArticles.Source
    url: str          # RSS URL
    category: str     # informational label for logging/reporting


# Sri Lankan English outlets: agriculture, business and economy sections.
_SL_FEEDS: list[FeedSpec] = [
    {
        "source": "economy_next",
        "url": "https://economynext.com/feed/",
        "category": "SL_economy",
    },
    {
        "source": "lbo",
        "url": "https://www.lankabusinessonline.com/feed/",
        "category": "SL_economy",
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

# Global and regional agriculture and commodity feeds.
_GLOBAL_AG_FEEDS: list[FeedSpec] = [
    {
        # ReliefWeb's per-country RSS path now 301-redirects to the updates river;
        # primary_country=144 is Sri Lanka.  Old /country/lka/rss.xml path is dead.
        "source": "reliefweb_lka",
        "url": "https://reliefweb.int/updates/rss.xml?primary_country=144",
        "category": "SL_disaster_weather",
    },
    {
        "source": "agrifarming_in",
        "url": "https://www.agrifarming.in/feed",
        "category": "regional_ag",
    },
]

# Public export.
FEEDS: list[FeedSpec] = _SL_FEEDS + _GLOBAL_AG_FEEDS


def feeds_by_category() -> dict[str, list[FeedSpec]]:
    """Return feeds grouped by category (for reporting / diagnostics)."""
    result: dict[str, list[FeedSpec]] = {}
    for f in FEEDS:
        result.setdefault(f["category"], []).append(f)
    return result
