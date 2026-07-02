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

Feed health (live-probed 2026-06-30).  Removed feeds that no longer yield RSS:
  - reuters_biz       feeds.reuters.com decommissioned (DNS no longer resolves)
  - worldbank_food    feeds.worldbank.org decommissioned (DNS no longer resolves)
  - fao_news          www.fao.org/feeds/rss/en/news/ returns 404
  - daily_ft          www.ft.lk/rss/ now serves a JS-gated HTML page, not RSS
  - news_first        newsfirst.lk/feed/ returns Cloudflare 403 to all bots
  - daily_mirror_lk   dailymirror.lk/rss.xml returns Cloudflare 403 to all bots
The two Cloudflare-403 outlets are good content sources but cannot be reached by
a plain HTTP fetcher; revisit only if we add a headless-browser fetch path.
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

# ---------------------------------------------------------------------------
# Global / regional agriculture & commodity feeds
# ---------------------------------------------------------------------------
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
