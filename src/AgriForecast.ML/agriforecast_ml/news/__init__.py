"""News ingestion subpackage.

Components:
  feeds            -- curated list of free English RSS feeds for SL agriculture / commodities
  fetcher          -- fetch + parse feeds with feedparser (resilient: per-feed error handling)
  loader           -- idempotent upsert into the Python-owned NewsArticles SQL table
  qa               -- basic QA: counts, date coverage, dedup assertion
  sentiment        -- VADER compound scoring + topic flags + daily aggregation (Chunk C2)
  store_sentiment  -- full rebuild of the Python-owned NewsSentimentDaily table (Chunk C2)

Phase 5 Chunk C1 -- raw ingestion (NewsArticles).
Phase 5 Chunk C2 -- sentiment scoring -> NewsSentimentDaily (national daily signal).
Chunk D as-of-joins NewsSentimentDaily into the feature store.
"""
