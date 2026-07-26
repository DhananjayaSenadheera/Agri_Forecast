"""NLP sentiment scoring + topic flagging for ingested news articles (Chunk C2).

Pipeline:
  1. Per-article scoring (score_articles):
       - VADER compound score on `Title + " " + Summary`.
       - Topic flags via word-boundary keyword matching (case-insensitive).
       - Effective article date = COALESCE(PublishedDateUtc, RetrievedAtUtc).date()
         -- conservative, never future-dated.
  2. Daily national aggregation (aggregate_daily):
       - One row per calendar date: mean VADER compound, article count, and a
         count + ratio per topic flag.  This is the NATIONAL signal -- identical
         across all crops; Chunk D as-of-joins it into the feature store.

Engine choice: VADER (lexicon, English-only) -- locked Phase 5 decision.
  Cheap, deterministic, no model download, good enough for a noisy signal on
  ~13 months of thin data.  NOT a transformer.

Topic flags: simple keyword presence (word-boundary aware).  The keyword lists
  live in TOPIC_KEYWORDS below -- one place, easy to extend.

LEAKAGE NOTE (for Chunk D):
  The daily table is keyed by the article's *effective date* (COALESCE above).
  An article is only attributable to dates >= its effective date.  Chunk D must
  perform a BACKWARD as-of join (sentiment for feature-date D uses only rows
  with Date <= D).  This module never future-dates an article (RetrievedAtUtc is
  always <= "now", and PublishedDateUtc is the real pub date), so the backward
  join is the safeguard.  Do NOT forward-fill across the join boundary.
"""
from __future__ import annotations

import logging
import re
from typing import Iterable

import pandas as pd
from vaderSentiment.vaderSentiment import SentimentIntensityAnalyzer

logger = logging.getLogger(__name__)

# Topic keyword lists - one place, easy to extend. A topic fires if ANY of its keywords
# match, case-insensitively and on word boundaries, so 'pest' does not match 'pesticide'
# (a different concept - conflating them muddies the signal). Multi-word phrases are
# matched as a phrase with boundaries at each end.
TOPIC_KEYWORDS: dict[str, list[str]] = {
    "pest": ["pest", "pests", "infestation", "armyworm", "locust", "locusts"],
    "flood": ["flood", "floods", "flooding", "flooded", "inundation"],
    "drought": ["drought", "droughts", "dry spell", "water shortage"],
    "policy": [
        "policy",
        "subsidy",
        "subsidies",
        "tariff",
        "tariffs",
        "regulation",
        "government",
        "ministry",
    ],
    # Cheap agri-relevant extras:
    "fertiliser": ["fertiliser", "fertilisers", "fertilizer", "fertilizers"],
    "import_ban": ["import ban", "import bans", "import restriction", "import restrictions"],
}

# Public ordering of topics, so callers and the table schema see a stable column order.
TOPIC_NAMES: list[str] = list(TOPIC_KEYWORDS.keys())


def _make_topic_matcher() -> dict[str, "re.Pattern[str]"]:
    """Compile one case-insensitive, word-boundary regex per topic.

    Keywords within a topic are OR-ed.  Each keyword is regex-escaped and
    wrapped in \\b...\\b so "pest" does not match "pesticide" but "pest control"
    still fires the pest flag (the standalone token "pest" is present).
    """
    matchers: dict[str, re.Pattern[str]] = {}
    for topic, keywords in TOPIC_KEYWORDS.items():
        alternatives = [r"\b" + re.escape(kw) + r"\b" for kw in keywords]
        pattern = "|".join(alternatives)
        matchers[topic] = re.compile(pattern, flags=re.IGNORECASE)
    return matchers


_TOPIC_MATCHERS = _make_topic_matcher()


def topic_flags(text: str) -> dict[str, bool]:
    """Return {topic: bool} for a single text using the compiled matchers."""
    text = text or ""
    return {topic: bool(rx.search(text)) for topic, rx in _TOPIC_MATCHERS.items()}


# VADER analyzer is stateless + deterministic; build once.
_ANALYZER = SentimentIntensityAnalyzer()


def compound_score(text: str) -> float:
    """VADER compound score in [-1, 1] for a single text.  Empty -> 0.0."""
    text = (text or "").strip()
    if not text:
        return 0.0
    return float(_ANALYZER.polarity_scores(text)["compound"])


def _combined_text(title: object, summary: object) -> str:
    """`Title + " " + Summary`, tolerating NULL/NaN on either side."""
    t = "" if title is None or (isinstance(title, float) and pd.isna(title)) else str(title)
    s = "" if summary is None or (isinstance(summary, float) and pd.isna(summary)) else str(summary)
    return (t + " " + s).strip()


def score_articles(articles: pd.DataFrame) -> pd.DataFrame:
    """Score each article: compound sentiment + topic flags + effective date.

    Args:
        articles: DataFrame with at least columns Title, Summary,
                  PublishedDateUtc (nullable), RetrievedAtUtc (not null).

    Returns:
        A copy with added columns:
          SentimentScore : float  -- VADER compound on Title+" "+Summary
          EffectiveDate  : date   -- COALESCE(PublishedDateUtc, RetrievedAtUtc).date()
          <topic>        : bool   -- one column per topic in TOPIC_NAMES
    """
    out = articles.copy()

    if out.empty:
        out["SentimentScore"] = pd.Series(dtype="float64")
        out["EffectiveDate"] = pd.Series(dtype="object")
        for topic in TOPIC_NAMES:
            out[topic] = pd.Series(dtype="bool")
        return out

    texts = [
        _combined_text(t, s)
        for t, s in zip(out["Title"].tolist(), out["Summary"].tolist())
    ]

    out["SentimentScore"] = [compound_score(t) for t in texts]

    flags = [topic_flags(t) for t in texts]
    for topic in TOPIC_NAMES:
        out[topic] = [f[topic] for f in flags]

    # Effective date = COALESCE(PublishedDateUtc, RetrievedAtUtc), as a date.
    pub = pd.to_datetime(out["PublishedDateUtc"], errors="coerce", utc=False)
    ret = pd.to_datetime(out["RetrievedAtUtc"], errors="coerce", utc=False)
    effective = pub.fillna(ret)
    out["EffectiveDate"] = effective.dt.date

    return out


def aggregate_daily(scored: pd.DataFrame) -> pd.DataFrame:
    """Aggregate per-article scores into the national daily signal.

    One row per EffectiveDate.  Days with no articles are simply absent (Chunk D
    as-of-joins, so gaps are expected and fine).

    Returns columns (see NewsSentimentDaily schema in store_sentiment.py):
        Date           : date
        MeanSentiment  : float  -- mean VADER compound that day
        ArticleCount   : int
        <Topic>Count   : int    -- # articles that day with the flag set
        <Topic>Ratio   : float  -- <Topic>Count / ArticleCount  (0..1)

    Topic column names are PascalCase derived from TOPIC_NAMES, e.g.
        pest -> PestCount / PestRatio
        import_ban -> ImportBanCount / ImportBanRatio
    """
    if scored.empty:
        cols = ["Date", "MeanSentiment", "ArticleCount"]
        for topic in TOPIC_NAMES:
            pas = _pascal(topic)
            cols += [f"{pas}Count", f"{pas}Ratio"]
        return pd.DataFrame(columns=cols)

    grp = scored.groupby("EffectiveDate", sort=True)

    daily = grp.agg(
        MeanSentiment=("SentimentScore", "mean"),
        ArticleCount=("SentimentScore", "size"),
    ).reset_index()
    daily = daily.rename(columns={"EffectiveDate": "Date"})

    for topic in TOPIC_NAMES:
        pas = _pascal(topic)
        # bool sum == count of True
        counts = grp[topic].sum().reset_index(drop=True)
        daily[f"{pas}Count"] = counts.astype("int64").values
        daily[f"{pas}Ratio"] = (
            daily[f"{pas}Count"] / daily["ArticleCount"]
        ).astype("float64")

    daily["MeanSentiment"] = daily["MeanSentiment"].astype("float64")
    daily["ArticleCount"] = daily["ArticleCount"].astype("int64")
    daily = daily.sort_values("Date").reset_index(drop=True)
    return daily


def _pascal(snake: str) -> str:
    """import_ban -> ImportBan ; pest -> Pest."""
    return "".join(part.capitalize() for part in snake.split("_"))


def topics_csv(flags: "dict[str, bool]") -> str:
    """Fired topics as a stable CSV string ('' when none fired).

    Order is TOPIC_NAMES (the one documented ordering), so the same article
    always serialises identically — the string is stored on NewsArticles.Topics
    and consumed verbatim by the admin News feed (.NET reads it, FE splits on
    ','). Empty string (not NULL) = scored-and-general, distinguishing it from
    NULL = not yet scored.
    """
    return ",".join(topic for topic in TOPIC_NAMES if flags.get(topic))


def score_and_aggregate(articles: pd.DataFrame) -> "tuple[pd.DataFrame, pd.DataFrame]":
    """Convenience: score_articles then aggregate_daily.

    Returns (scored_articles, daily_signal).
    """
    scored = score_articles(articles)
    daily = aggregate_daily(scored)
    return scored, daily
