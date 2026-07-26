"""Persist the daily national sentiment signal to SQL Server (full rebuild).

Table ownership:
  NewsSentimentDaily is a Python-owned ML table (created here, not via EF
  migrations).  It mirrors store.py / CropFeatureDaily conventions:
    - full idempotent rebuild via to_sql(if_exists="replace")
    - sized dtypes so the key column is indexable
    - explicit index on Date
  Re-running is deterministic: the table is dropped+rebuilt from NewsArticles.

Schema (NewsSentimentDaily) -- this is the contract Chunk D consumes:
  Date            DATE      PK   -- calendar date (article EffectiveDate)
  MeanSentiment   FLOAT          -- mean VADER compound that day, in [-1, 1]
  ArticleCount    INT            -- # articles attributed to that date
  PestCount       INT            -- topic counts (one per TOPIC_NAMES flag)
  PestRatio       FLOAT          -- PestCount / ArticleCount, in [0, 1]
  FloodCount / FloodRatio
  DroughtCount / DroughtRatio
  PolicyCount / PolicyRatio
  FertiliserCount / FertiliserRatio
  ImportBanCount / ImportBanRatio

  Days with no articles are ABSENT (not zero-filled) -- Chunk D as-of-joins, so
  gaps are intended.

Optional debug writeback:
  add_sentiment_score_column() + write_article_scores() populate
  NewsArticles.SentimentScore (a column C1 reserved).  This is purely for
  debugging; the daily table above is the real deliverable.
"""
from __future__ import annotations

import logging

import pandas as pd
from sqlalchemy import text
from sqlalchemy.types import DATE, FLOAT, INTEGER

from ..db import get_engine
from .sentiment import TOPIC_NAMES, _pascal

logger = logging.getLogger(__name__)

TABLE = "NewsSentimentDaily"
ARTICLES_TABLE = "NewsArticles"


def _dtype_map() -> dict:
    """Explicit SQL types so Date is a proper DATE PK and floats/ints are sized."""
    dtypes: dict = {
        "Date": DATE(),
        "MeanSentiment": FLOAT(),
        "ArticleCount": INTEGER(),
    }
    for topic in TOPIC_NAMES:
        pas = _pascal(topic)
        dtypes[f"{pas}Count"] = INTEGER()
        dtypes[f"{pas}Ratio"] = FLOAT()
    return dtypes


def write_daily(daily: pd.DataFrame, *, engine=None) -> int:
    """Full idempotent rebuild of NewsSentimentDaily from a daily DataFrame.

    Args:
        daily: Output of sentiment.aggregate_daily (one row per Date).
        engine: SQLAlchemy engine; created from config if None.

    Returns:
        Number of rows written.
    """
    eng = engine or get_engine()
    out = daily.copy()

    out.to_sql(
        TABLE,
        eng,
        if_exists="replace",
        index=False,
        chunksize=200,
        method="multi",
        dtype=_dtype_map(),
    )

    # Add a clustered PK / index on Date (replace drops it each rebuild).
    with eng.begin() as conn:
        conn.execute(text(
            f"CREATE UNIQUE CLUSTERED INDEX IX_{TABLE}_Date ON {TABLE} (Date)"
        ))

    logger.info("write_daily: rebuilt %s with %d rows", TABLE, len(out))
    return len(out)


# Per-article writeback of NewsArticles.SentimentScore and .Topics. The admin News feed
# surfaces both per article, so score_news writes them on every pass. NULL means not yet
# scored; an empty Topics string means scored with no agri topic fired.
_DDL_ADD_SCORE_COLUMN = f"""IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = '{ARTICLES_TABLE}' AND COLUMN_NAME = 'SentimentScore'
)
BEGIN
    ALTER TABLE {ARTICLES_TABLE} ADD SentimentScore FLOAT NULL;
END
"""

# NVARCHAR(100) comfortably fits every topic CSV (all six topics joined = 44 chars).
_DDL_ADD_TOPICS_COLUMN = f"""IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME = '{ARTICLES_TABLE}' AND COLUMN_NAME = 'Topics'
)
BEGIN
    ALTER TABLE {ARTICLES_TABLE} ADD Topics NVARCHAR(100) NULL;
END
"""


def add_sentiment_score_column(*, engine=None) -> None:
    """Idempotently add the per-article signal columns if missing.

    (Name kept for backwards compatibility; it now ensures BOTH SentimentScore
    and Topics.)
    """
    eng = engine or get_engine()
    with eng.begin() as conn:
        conn.execute(text(_DDL_ADD_SCORE_COLUMN))
        conn.execute(text(_DDL_ADD_TOPICS_COLUMN))
    logger.debug(
        "add_sentiment_score_column: %s.SentimentScore + .Topics ready", ARTICLES_TABLE
    )


def write_article_scores(scored: pd.DataFrame, *, engine=None) -> int:
    """Write per-article SentimentScore + Topics CSV back to NewsArticles.

    Args:
        scored: Output of sentiment.score_articles -- must have Url +
                SentimentScore + one bool column per TOPIC_NAMES topic.
        engine: SQLAlchemy engine; created from config if None.

    Returns:
        Number of rows updated.
    """
    from .sentiment import TOPIC_NAMES as _topics, topics_csv

    eng = engine or get_engine()
    add_sentiment_score_column(engine=eng)

    if scored.empty:
        return 0

    updated = 0
    with eng.begin() as conn:
        for row in scored.to_dict("records"):
            flags = {topic: bool(row.get(topic)) for topic in _topics}
            conn.execute(
                text(
                    f"UPDATE {ARTICLES_TABLE} "
                    "SET SentimentScore = :score, Topics = :topics "
                    "WHERE Url = :url"
                ),
                {
                    "score": float(row["SentimentScore"]),
                    "topics": topics_csv(flags),
                    "url": row["Url"],
                },
            )
            updated += 1
    logger.info("write_article_scores: updated %d rows (score + topics)", updated)
    return updated


# Read side: load NewsArticles so they can be scored.
def load_articles(*, engine=None) -> pd.DataFrame:
    """Load NewsArticles for scoring.  Returns Url, Title, Summary, dates."""
    eng = engine or get_engine()
    sql = f"""
        SELECT Url, Source, Title, Summary, PublishedDateUtc, RetrievedAtUtc
        FROM {ARTICLES_TABLE}
    """
    df = pd.read_sql(sql, eng)
    return df
