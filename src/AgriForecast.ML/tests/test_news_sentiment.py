"""
AgriForecast ML -- News sentiment tests (Phase 5 Chunk C2).

Coverage (all fast, no live network, no live DB):
  TestVaderSign        -- VADER compound sign on known positive/negative text.
  TestTopicFlags       -- keyword matching incl. word-boundary (pest vs pesticide).
  TestScoreArticles    -- per-article scoring adds expected columns + COALESCE date.
  TestAggregateDaily   -- daily aggregation correctness on a small DataFrame.
  TestIdempotency      -- re-running score+aggregate is deterministic.
  TestCoalesceDate     -- null PublishedDateUtc falls on RetrievedAtUtc's date.
  TestEmpty            -- empty input degrades gracefully (no crash, right columns).
"""
from __future__ import annotations

import sys
from datetime import datetime
from pathlib import Path

import pandas as pd
import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml.news import sentiment  # noqa: E402
from agriforecast_ml.news.sentiment import (  # noqa: E402
    TOPIC_NAMES,
    _pascal,
    aggregate_daily,
    compound_score,
    score_articles,
    topic_flags,
)


# ---------------------------------------------------------------------------
# Helpers / fixtures
# ---------------------------------------------------------------------------

def _make_articles() -> pd.DataFrame:
    """Small in-memory NewsArticles-shaped DataFrame for aggregation tests."""
    return pd.DataFrame(
        [
            # Day 2026-06-01: 2 articles, one pest+flood, one neutral.
            {
                "Url": "u1",
                "Title": "Pest outbreak devastates paddy crops amid flooding",
                "Summary": "Farmers report severe losses.",
                "PublishedDateUtc": datetime(2026, 6, 1, 8, 0, 0),
                "RetrievedAtUtc": datetime(2026, 6, 2, 0, 0, 0),
            },
            {
                "Url": "u2",
                "Title": "Market report for vegetables",
                "Summary": "Prices steady at the economic center.",
                "PublishedDateUtc": datetime(2026, 6, 1, 9, 0, 0),
                "RetrievedAtUtc": datetime(2026, 6, 2, 0, 0, 0),
            },
            # Day 2026-06-03: 1 article, drought + policy, null pub date
            # -> falls on RetrievedAtUtc date (2026-06-03).
            {
                "Url": "u3",
                "Title": "Government announces drought relief subsidy policy",
                "Summary": "Ministry pledges support.",
                "PublishedDateUtc": None,
                "RetrievedAtUtc": datetime(2026, 6, 3, 12, 0, 0),
            },
        ]
    )


# ===========================================================================
# TestVaderSign
# ===========================================================================

class TestVaderSign:
    def test_positive_sentence(self):
        s = compound_score("This is wonderful, excellent news for farmers!")
        assert s > 0.3, f"Expected clearly positive, got {s}"

    def test_negative_sentence(self):
        s = compound_score("This is a terrible, devastating disaster for crops.")
        assert s < -0.3, f"Expected clearly negative, got {s}"

    def test_empty_is_zero(self):
        assert compound_score("") == 0.0
        assert compound_score("   ") == 0.0

    def test_score_in_range(self):
        for txt in ["great", "awful", "the the the", "rain expected tomorrow"]:
            s = compound_score(txt)
            assert -1.0 <= s <= 1.0


# ===========================================================================
# TestTopicFlags
# ===========================================================================

class TestTopicFlags:
    def test_pest_matches(self):
        assert topic_flags("A pest destroyed the field")["pest"] is True

    def test_pesticide_does_not_match_pest(self):
        # Word-boundary rule: "pesticide" must NOT fire the pest flag.
        flags = topic_flags("New pesticide approved for use")
        assert flags["pest"] is False, "pesticide should not match \\bpest\\b"

    def test_pest_phrase_still_matches(self):
        # "pest control" contains the standalone token "pest" -> fires.
        assert topic_flags("pest control measures rolled out")["pest"] is True

    def test_case_insensitive(self):
        assert topic_flags("FLOOD warnings issued")["flood"] is True
        assert topic_flags("Drought Conditions Worsen")["drought"] is True

    def test_policy_keywords(self):
        assert topic_flags("New government subsidy announced")["policy"] is True

    def test_fertiliser_both_spellings(self):
        assert topic_flags("fertiliser shortage")["fertiliser"] is True
        assert topic_flags("fertilizer prices rise")["fertiliser"] is True

    def test_import_ban_phrase(self):
        assert topic_flags("Import ban on onions lifted")["import_ban"] is True
        # Single word "import" alone should not fire import_ban.
        assert topic_flags("import volumes rose")["import_ban"] is False

    def test_no_false_positive(self):
        flags = topic_flags("Sunny weather boosts tomato harvest")
        assert not any(flags.values()), f"Expected no flags, got {flags}"

    def test_all_topics_present(self):
        flags = topic_flags("hello world")
        assert set(flags.keys()) == set(TOPIC_NAMES)


# ===========================================================================
# TestTopicsCsv -- the string persisted to NewsArticles.Topics (admin News feed
# contract: '' = scored/no topic; stable TOPIC_NAMES order; FE splits on ',').
# ===========================================================================

class TestTopicsCsv:
    def test_no_flags_is_empty_string(self):
        from agriforecast_ml.news.sentiment import topics_csv
        assert topics_csv({t: False for t in TOPIC_NAMES}) == ""

    def test_single_flag(self):
        from agriforecast_ml.news.sentiment import topics_csv
        flags = {t: (t == "flood") for t in TOPIC_NAMES}
        assert topics_csv(flags) == "flood"

    def test_order_is_topic_names_not_input_order(self):
        from agriforecast_ml.news.sentiment import topics_csv
        # All fired -> exact documented ordering, regardless of dict order.
        flags = {t: True for t in reversed(TOPIC_NAMES)}
        assert topics_csv(flags) == ",".join(TOPIC_NAMES)

    def test_missing_keys_treated_as_false(self):
        from agriforecast_ml.news.sentiment import topics_csv
        assert topics_csv({"pest": True}) == "pest"

    def test_roundtrips_with_topic_flags(self):
        from agriforecast_ml.news.sentiment import topics_csv
        csv = topics_csv(topic_flags("Flood damage after the drought broke"))
        assert csv == "flood,drought"


# ===========================================================================
# TestScoreArticles
# ===========================================================================

class TestScoreArticles:
    def test_adds_expected_columns(self):
        scored = score_articles(_make_articles())
        assert "SentimentScore" in scored.columns
        assert "EffectiveDate" in scored.columns
        for topic in TOPIC_NAMES:
            assert topic in scored.columns

    def test_scores_title_plus_summary(self):
        scored = score_articles(_make_articles())
        # First article is clearly negative (devastates, severe losses).
        assert scored.iloc[0]["SentimentScore"] < 0

    def test_pest_and_flood_flagged(self):
        scored = score_articles(_make_articles())
        row = scored[scored["Url"] == "u1"].iloc[0]
        assert row["pest"] is True or row["pest"] == True  # noqa: E712
        assert row["flood"] == True  # noqa: E712


# ===========================================================================
# TestCoalesceDate
# ===========================================================================

class TestCoalesceDate:
    def test_uses_published_when_present(self):
        scored = score_articles(_make_articles())
        row = scored[scored["Url"] == "u1"].iloc[0]
        assert str(row["EffectiveDate"]) == "2026-06-01"

    def test_falls_back_to_retrieved_when_null_pub(self):
        scored = score_articles(_make_articles())
        row = scored[scored["Url"] == "u3"].iloc[0]
        # PublishedDateUtc is None -> RetrievedAtUtc date 2026-06-03.
        assert str(row["EffectiveDate"]) == "2026-06-03"


# ===========================================================================
# TestAggregateDaily
# ===========================================================================

class TestAggregateDaily:
    def test_one_row_per_date(self):
        _, daily = sentiment.score_and_aggregate(_make_articles())
        assert len(daily) == 2  # 2026-06-01 and 2026-06-03
        assert list(daily["Date"].astype(str)) == ["2026-06-01", "2026-06-03"]

    def test_article_counts(self):
        _, daily = sentiment.score_and_aggregate(_make_articles())
        d = daily.set_index(daily["Date"].astype(str))
        assert d.loc["2026-06-01", "ArticleCount"] == 2
        assert d.loc["2026-06-03", "ArticleCount"] == 1

    def test_topic_counts_and_ratios(self):
        _, daily = sentiment.score_and_aggregate(_make_articles())
        d = daily.set_index(daily["Date"].astype(str))
        # Day 1: 1 of 2 articles is pest -> count 1, ratio 0.5
        assert d.loc["2026-06-01", "PestCount"] == 1
        assert d.loc["2026-06-01", "PestRatio"] == pytest.approx(0.5)
        assert d.loc["2026-06-01", "FloodCount"] == 1
        # Day 3: drought + policy
        assert d.loc["2026-06-03", "DroughtCount"] == 1
        assert d.loc["2026-06-03", "PolicyCount"] == 1
        assert d.loc["2026-06-03", "DroughtRatio"] == pytest.approx(1.0)

    def test_mean_sentiment_is_mean(self):
        scored, daily = sentiment.score_and_aggregate(_make_articles())
        day1 = scored[scored["EffectiveDate"].astype(str) == "2026-06-01"]
        expected = day1["SentimentScore"].mean()
        d = daily.set_index(daily["Date"].astype(str))
        assert d.loc["2026-06-01", "MeanSentiment"] == pytest.approx(expected)

    def test_schema_columns(self):
        _, daily = sentiment.score_and_aggregate(_make_articles())
        expected = ["Date", "MeanSentiment", "ArticleCount"]
        for topic in TOPIC_NAMES:
            pas = _pascal(topic)
            expected += [f"{pas}Count", f"{pas}Ratio"]
        assert list(daily.columns) == expected


# ===========================================================================
# TestIdempotency
# ===========================================================================

class TestIdempotency:
    def test_rebuild_deterministic(self):
        a = _make_articles()
        _, d1 = sentiment.score_and_aggregate(a)
        _, d2 = sentiment.score_and_aggregate(a.copy())
        pd.testing.assert_frame_equal(
            d1.reset_index(drop=True), d2.reset_index(drop=True)
        )

    def test_row_order_stable(self):
        _, daily = sentiment.score_and_aggregate(_make_articles())
        dates = list(daily["Date"].astype(str))
        assert dates == sorted(dates)


# ===========================================================================
# TestEmpty
# ===========================================================================

class TestEmpty:
    def test_empty_articles_no_crash(self):
        empty = pd.DataFrame(
            columns=["Url", "Title", "Summary", "PublishedDateUtc", "RetrievedAtUtc"]
        )
        scored, daily = sentiment.score_and_aggregate(empty)
        assert scored.empty
        assert daily.empty

    def test_empty_daily_has_schema(self):
        empty = pd.DataFrame(
            columns=["Url", "Title", "Summary", "PublishedDateUtc", "RetrievedAtUtc"]
        )
        _, daily = sentiment.score_and_aggregate(empty)
        assert "Date" in daily.columns
        assert "MeanSentiment" in daily.columns
        for topic in TOPIC_NAMES:
            assert f"{_pascal(topic)}Count" in daily.columns


# ===========================================================================
# TestPascal
# ===========================================================================

class TestPascal:
    def test_snake_to_pascal(self):
        assert _pascal("pest") == "Pest"
        assert _pascal("import_ban") == "ImportBan"
