"""Idempotent upsert of RawArticle records into the NewsArticles SQL table.

Table ownership:
  NewsArticles is a Python-owned ML table (created here, not via EF migrations).
  It follows the same pattern as CropFeatureDaily -- sized NVARCHAR dtypes,
  explicit indices, created if not present.

Dedup strategy:
  Url is the unique key.  On re-run, articles whose Url already exists in the
  table are skipped (counted as 'dup_skipped').  We do NOT update existing rows
  on re-run -- the raw text and publication date are immutable once stored.

Schema (NewsArticles):
  Url                NVARCHAR(2048)  PK / UNIQUE
  Source             NVARCHAR(100)
  Title              NVARCHAR(500)
  Summary            NVARCHAR(1000)
  PublishedDateUtc   DATETIME2       nullable (some feeds omit pub date)
  RetrievedAtUtc     DATETIME2       NOT NULL
  Language           NVARCHAR(10)    always 'en' at this stage

Indices:
  PK_NewsArticles         Clustered PK on Url
  UQ_NewsArticles_Url     UNIQUE on Url (explicit, belt-and-suspenders)
  IX_NewsArticles_PubDate Non-clustered on PublishedDateUtc (time-windowed joins)
"""
from __future__ import annotations

import logging
from datetime import datetime, timezone

import sqlalchemy as sa
from sqlalchemy import text

from ..db import get_engine
from .fetcher import RawArticle

logger = logging.getLogger(__name__)

TABLE = "NewsArticles"


def _to_naive_utc(dt: "datetime | None") -> "datetime | None":
    """Coerce a tz-aware UTC datetime to naive UTC for the *Utc DB columns.

    Timezone convention: NewsArticles.PublishedDateUtc / RetrievedAtUtc store
    naive UTC wall-clock. The fetcher always produces tz-aware UTC datetimes
    (or None); SQL Server DATETIME2 is tz-naive, so we drop tzinfo here at the
    DB boundary. Without this the ODBC driver may reinterpret a tz-aware value
    in server-local time and shift the stored timestamp.
    """
    if dt is None:
        return None
    if dt.tzinfo is not None:
        dt = dt.astimezone(timezone.utc).replace(tzinfo=None)
    return dt

# IF NOT EXISTS guard: safe to call on every run (idempotent DDL).
_DDL_CREATE_TABLE = """IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.TABLES
    WHERE TABLE_NAME = \'NewsArticles\'
)
BEGIN
    CREATE TABLE NewsArticles (
        Url              NVARCHAR(2048) NOT NULL,
        Source           NVARCHAR(100)  NOT NULL,
        Title            NVARCHAR(500)  NOT NULL,
        Summary          NVARCHAR(1000) NOT NULL,
        PublishedDateUtc DATETIME2      NULL,
        RetrievedAtUtc   DATETIME2      NOT NULL,
        Language         NVARCHAR(10)   NOT NULL
            CONSTRAINT DF_NewsArticles_Language DEFAULT (\'en\'),
        CONSTRAINT PK_NewsArticles PRIMARY KEY CLUSTERED (Url)
    );

    CREATE UNIQUE INDEX UQ_NewsArticles_Url
        ON NewsArticles (Url);

    CREATE INDEX IX_NewsArticles_PubDate
        ON NewsArticles (PublishedDateUtc);
END
"""


def ensure_table(engine: "sa.engine.Engine | None" = None) -> None:
    """Create NewsArticles table + indices if they do not already exist.

    Idempotent: safe to call on every run.  Uses IF NOT EXISTS so it never
    errors on a second call.
    """
    eng = engine or get_engine()
    with eng.begin() as conn:
        conn.execute(text(_DDL_CREATE_TABLE))
    logger.debug("ensure_table: %s ready", TABLE)


def upsert_articles(
    articles: "list[RawArticle]",
    *,
    engine: "sa.engine.Engine | None" = None,
) -> dict:
    """Insert new articles; skip URLs already present in NewsArticles.

    Args:
        articles: Output of fetcher.fetch_all() or fetcher.fetch_feed().
        engine:   SQLAlchemy engine; created from config if None.

    Returns:
        Dict with counts:
          inserted     -- new rows written
          dup_skipped  -- URLs already in the table, skipped
          total        -- len(articles)
    """
    if not articles:
        return {"inserted": 0, "dup_skipped": 0, "total": 0}

    eng = engine or get_engine()
    ensure_table(eng)

    counters: dict = {"inserted": 0, "dup_skipped": 0, "total": len(articles)}

    with eng.begin() as conn:
        # Fetch the set of Urls already stored for the candidate batch.
        # Chunk to avoid exceeding SQL Server's parameter limit (2100).
        candidate_urls = [a["url"] for a in articles]
        existing_urls: set[str] = set()

        chunk_size = 200
        for i in range(0, len(candidate_urls), chunk_size):
            chunk = candidate_urls[i : i + chunk_size]
            placeholders = ", ".join(f":u{j}" for j in range(len(chunk)))
            params = {f"u{j}": url for j, url in enumerate(chunk)}
            rows = conn.execute(
                text(f"SELECT Url FROM {TABLE} WHERE Url IN ({placeholders})"),
                params,
            ).fetchall()
            for row in rows:
                existing_urls.add(row[0])

        for art in articles:
            url = art["url"]
            if url in existing_urls:
                counters["dup_skipped"] += 1
                logger.debug("DUP skipped: %s", url)
                continue

            conn.execute(
                text(
                    f"INSERT INTO {TABLE} "
                    "(Url, Source, Title, Summary, "
                    "PublishedDateUtc, RetrievedAtUtc, Language) "
                    "VALUES "
                    "(:url, :source, :title, :summary, "
                    ":pub_date, :retrieved, :lang)"
                ),
                {
                    "url": url,
                    "source": art["source"],
                    "title": art["title"],
                    "summary": art["summary"],
                    "pub_date": _to_naive_utc(art["published_utc"]),
                    "retrieved": _to_naive_utc(art["retrieved_utc"]),
                    "lang": art["language"],
                },
            )
            existing_urls.add(url)   # guard against dupes within this batch
            counters["inserted"] += 1

    logger.info(
        "upsert_articles: total=%d inserted=%d dup_skipped=%d",
        counters["total"], counters["inserted"], counters["dup_skipped"],
    )
    return counters
