"""
AgriForecast ML — multi-market spread feature contract tests (R1.1 P4 step 2,
ClickUp 86caheffr).

Written FIRST, before any spread-feature code exists. This file only pins the
contract of ``get_feature_safe_market_ids()`` (canonical.py:347) -- the
function that will define "national" (unweighted mean over feature-safe
markets) for the upcoming ``_attach_market_spread`` feature. No spread
features are implemented or tested here; this is the foundation the
leakage/boundary suite for step 2 will grow into (see CONTRACTS.md 2026-07-04
P4 contracts).

Structured like test_festivals.py / test_macro_vintage.py: hermetic tests
already exist for this function in test_canonical.py::TestFeatureSafeMarketIds
(mocked engine, asserts the SQL WHERE clause). This file adds the missing
LIVE-DB layer: what the real Markets table actually contains today, gated
with the same skip-on-unreachable convention as _db_or_skip() elsewhere
(never fail the suite because a dev machine has no DB configured).

Live-DB findings pinned at P4 (2026-07-04) — SUPERSEDED by R2 Step 6 reseed:
  P4 state (historical): 10 Markets = 4x ECOMAP-% demo twins + 1x CBSL
  NationalAggregate + 5x real feature-safe (Pettah/Narahenpita/Dambulla/
  Keppetipola/Thambuttegama).
  R2 Step 6 (2026-07-07) reseeded Markets to 12 rows (all real HARTI/DEC
  markets) and RETIRED the ECOMAP-%/DMB###### demo-twin scheme, so the live DB
  now has ZERO ECOMAP-% rows and 11 feature-safe markets (Dambulla, Keppetipola,
  Thambuttegama, Pettah, Narahenpita, Kandy, Meegoda, Norochchole, Nuwara Eliya,
  Bandarawela, Veyangoda); only the CBSL NationalAggregate is excluded. These
  live-contract tests are DB-DRIVEN (get_feature_safe_market_ids reads Markets),
  so they track the current seed; the ECOMAP-exclusion test now skips when no
  ECOMAP rows exist rather than asserting a stale P4 seed-count precondition.
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest
import sqlalchemy as sa

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import canonical  # noqa: E402
import numpy as np  # noqa: E402
import pandas as pd  # noqa: E402

from agriforecast_ml import features as _features  # noqa: E402
from agriforecast_ml import load as _load  # noqa: E402


# DB-gated helper (mirrors test_festivals.py::_db_or_skip / test_macro_vintage
# convention: skip, never fail, when the live DB is unreachable)

def _db_or_skip():
    try:
        from agriforecast_ml.envfile import load_env_file
        from agriforecast_ml.db import get_engine

        load_env_file()
        engine = get_engine()
        with engine.connect() as conn:
            conn.execute(sa.text("SELECT 1"))
    except Exception as e:  # pragma: no cover - env-dependent
        pytest.skip(f"DB unreachable: {e}")
    return engine


def _all_markets(engine):
    with engine.connect() as conn:
        rows = conn.execute(sa.text(
            "SELECT Id, Name, MarketType, MarketCode FROM Markets"
        )).fetchall()
    return rows


# Contract test: get_feature_safe_market_ids() live-DB behaviour

class TestFeatureSafeMarketIdsLiveContract:
    """Live-DB counterpart to test_canonical.py::TestFeatureSafeMarketIds
    (which only verifies the SQL text against a mocked engine). This class
    proves the exclusion rule actually holds against the real Markets table
    contents, so a future change to the WHERE clause -- or to seeded data --
    that silently re-admits a NationalAggregate/ECOMAP row would be caught
    here even though the mocked-engine test would still pass (it only checks
    the query string, not real row content)."""

    def test_excludes_every_national_aggregate_market(self):
        """No MarketId with MarketType == 3 (NationalAggregate) may appear in
        the feature-safe set."""
        engine = _db_or_skip()
        all_markets = _all_markets(engine)
        national_aggregate_ids = {
            canonical._parse_guid(r[0]) for r in all_markets if r[2] == 3
        }
        assert national_aggregate_ids, (
            "expected at least one MarketType=3 (NationalAggregate) row in "
            "the live Markets table -- test fixture assumption violated, "
            "re-verify DB seed state"
        )

        result = canonical.get_feature_safe_market_ids(engine)

        assert result.isdisjoint(national_aggregate_ids), (
            f"get_feature_safe_market_ids() must exclude every "
            f"MarketType=3 row; found overlap: "
            f"{result & national_aggregate_ids}"
        )

    def test_excludes_every_ecomap_twin_market(self):
        """No MarketId whose MarketCode starts with 'ECOMAP-' (the
        Economic-Center-Map synthetic demo/seed twin markets, e.g. the
        Pettah-adjacent double-count risk) may appear in the feature-safe
        set."""
        engine = _db_or_skip()
        all_markets = _all_markets(engine)
        ecomap_ids = {
            canonical._parse_guid(r[0])
            for r in all_markets
            if (r[3] or "").startswith("ECOMAP-")
        }
        # R2 Step 6 reseeded the Markets table (6 -> 12 real HARTI/DEC markets) and
        # the DMB######/ECOMAP demo-twin scheme was RETIRED, so the live DB may now
        # contain zero ECOMAP-% rows. The exclusion INVARIANT is what this test
        # guards; with no ECOMAP rows to admit there is nothing to assert against, so
        # skip rather than fail on a stale P4-era seed-count assumption. (The
        # NationalAggregate exclusion is covered by its own live-contract test;
        # the ECOMAP exclusion SQL itself is asserted data-independently by the
        # mocked-query test in test_canonical.py, so this skip loses no coverage.)
        if not ecomap_ids:
            pytest.skip("no ECOMAP-% markets in the live DB (retired in R2 Step 6); "
                        "exclusion invariant has nothing to admit")

        result = canonical.get_feature_safe_market_ids(engine)

        assert result.isdisjoint(ecomap_ids), (
            f"get_feature_safe_market_ids() must exclude every ECOMAP-% "
            f"coded market; found overlap: {result & ecomap_ids}"
        )

    def test_covered_markets_with_real_price_observations_remain_included(self):
        """The markets that actually have PriceObservations rows today must
        still be present in the feature-safe set -- proves the exclusion
        rule is narrow (type/code-based) and does not accidentally sweep up
        a real, populated market. Asserts on what the live DB actually shows
        (queried via COUNT(PriceObservations) per market), not a hardcoded
        assumption of which 4 markets those are."""
        engine = _db_or_skip()
        with engine.connect() as conn:
            rows = conn.execute(sa.text(
                """
                SELECT m.Id, m.Name, COUNT(po.Id) AS n
                FROM Markets m
                LEFT JOIN PriceObservations po ON po.MarketId = m.Id
                GROUP BY m.Id, m.Name
                HAVING COUNT(po.Id) > 0
                """
            )).fetchall()
        covered_market_ids = {canonical._parse_guid(r[0]) for r in rows}
        assert covered_market_ids, (
            "expected at least one market with real PriceObservations rows -- "
            "test fixture assumption violated, re-verify DB seed/ingest state"
        )

        result = canonical.get_feature_safe_market_ids(engine)

        missing = covered_market_ids - result
        assert not missing, (
            f"markets with real PriceObservations coverage were wrongly "
            f"excluded from the feature-safe set: {missing}"
        )

    def test_result_is_non_empty_strict_subset_of_all_markets(self):
        """Sanity/shape contract: the feature-safe set must be non-empty
        (there must be SOME real markets left to aggregate over) and a
        STRICT subset of all Markets rows (something must actually be
        excluded -- if the set equals all markets, the exclusion filter is
        not firing at all)."""
        engine = _db_or_skip()
        all_markets = _all_markets(engine)
        all_ids = {canonical._parse_guid(r[0]) for r in all_markets}

        result = canonical.get_feature_safe_market_ids(engine)

        assert result, "feature-safe market set must not be empty"
        assert result < all_ids, (
            "feature-safe market set must be a STRICT subset of all Markets "
            "rows (some markets must be excluded) -- got equality, exclusion "
            "filter appears to not be firing"
        )


# Builder-level unit tests for features._attach_market_spread (P4 step 2).
#
# Hermetic (no DB): drive the function with synthetic PriceObservations frames
# so the point-in-time / staleness / NaN-not-0 / derived-summary contract is
# pinned without a live database. QA writes the fuller leakage suite on top of
# these (see the task plan).

_SLUGS = ["Dambulla", "Keppetipola", "Thambuttegama", "Pettah", "Narahenpita"]


def _po(rows):
    """Build a load_price_observations()-shaped long frame from tuples
    (slug, crop, date, avg_price)."""
    df = pd.DataFrame(rows, columns=["MarketSlug", "CropId", "ObservedDate", "AvgPrice"])
    df["ObservedDate"] = pd.to_datetime(df["ObservedDate"])
    df["AvgPrice"] = df["AvgPrice"].astype(float)
    return df


def _frame(rows):
    """Per-(crop, date) result frame from (crop, obs_date) tuples."""
    df = pd.DataFrame(rows, columns=["CropId", "ObservationDate"])
    df["ObservationDate"] = pd.to_datetime(df["ObservationDate"])
    return df


class TestAttachMarketSpreadBuilder:
    def test_national_is_unweighted_mean_and_spread_is_dambulla_minus_national(self):
        result = _frame([("A", "2025-01-10")])
        po = _po([
            ("Dambulla", "A", "2025-01-10", 100.0),
            ("Keppetipola", "A", "2025-01-10", 120.0),
            ("Pettah", "A", "2025-01-10", 110.0),
        ])
        out = _features._attach_market_spread(result, po, _SLUGS)
        r = out.iloc[0]
        assert r["MktDambullaAvgPrice"] == 100.0
        assert r["MktKeppetipolaAvgPrice"] == 120.0
        assert r["MktPettahAvgPrice"] == 110.0
        assert r["NMarketsReporting"] == 3
        # national = (100+120+110)/3 = 110 ; spread = Dambulla - national = -10
        assert r["SpreadVsNational"] == pytest.approx(-10.0)
        # Dambulla is cheapest -> rank = 1/3 (only itself <= itself)
        assert r["MarketRankPct"] == pytest.approx(1 / 3)

    def test_missing_market_is_nan_never_zero(self):
        result = _frame([("A", "2025-01-10")])
        po = _po([("Dambulla", "A", "2025-01-10", 100.0),
                  ("Keppetipola", "A", "2025-01-10", 120.0)])
        out = _features._attach_market_spread(result, po, _SLUGS)
        r = out.iloc[0]
        # Narahenpita/Thambuttegama/Pettah never reported -> NaN, not 0.
        for col in ("MktNarahenpitaAvgPrice", "MktThambuttegamaAvgPrice",
                    "MktPettahAvgPrice"):
            assert np.isnan(r[col]), f"{col} should be NaN, got {r[col]}"

    def test_fewer_than_two_markets_nulls_derived_features(self):
        result = _frame([("A", "2025-01-10")])
        po = _po([("Dambulla", "A", "2025-01-10", 100.0)])  # only one market
        out = _features._attach_market_spread(result, po, _SLUGS)
        r = out.iloc[0]
        assert r["NMarketsReporting"] == 1
        assert np.isnan(r["SpreadVsNational"])
        assert np.isnan(r["MarketRankPct"])
        assert np.isnan(r["LeaderMarketLag7"])

    def test_staleness_cap_nans_prices_older_than_14_days(self):
        # Observation 2025-02-01. Dambulla reported 2025-01-31 (1d old -> kept);
        # Keppetipola last reported 2025-01-10 (22d old -> stale -> NaN).
        result = _frame([("A", "2025-02-01")])
        po = _po([
            ("Dambulla", "A", "2025-01-31", 200.0),
            ("Keppetipola", "A", "2025-01-10", 120.0),
        ])
        out = _features._attach_market_spread(result, po, _SLUGS)
        r = out.iloc[0]
        assert r["MktDambullaAvgPrice"] == 200.0
        assert np.isnan(r["MktKeppetipolaAvgPrice"]), "22d-old price must be NaN'd"
        # only 1 market effectively reporting -> derived NaN
        assert r["NMarketsReporting"] == 1
        assert np.isnan(r["SpreadVsNational"])

    def test_staleness_boundary_exactly_14_days_is_kept(self):
        # Exactly 14 days old is still visible (strict '>' cap).
        result = _frame([("A", "2025-01-15")])
        po = _po([
            ("Dambulla", "A", "2025-01-01", 100.0),   # 14 days old -> kept
            ("Keppetipola", "A", "2025-01-15", 120.0),
        ])
        out = _features._attach_market_spread(result, po, _SLUGS)
        r = out.iloc[0]
        assert r["MktDambullaAvgPrice"] == 100.0
        assert r["NMarketsReporting"] == 2

    def test_non_covered_crop_all_nan(self):
        # Crop B has no PriceObservations at all -> every spread feature NaN,
        # n_markets_reporting == 0.
        result = _frame([("B", "2025-01-10"), ("B", "2025-01-11")])
        po = _po([("Dambulla", "A", "2025-01-10", 100.0)])  # only crop A
        out = _features._attach_market_spread(result, po, _SLUGS)
        for _, r in out.iterrows():
            assert r["NMarketsReporting"] == 0
            assert np.isnan(r["SpreadVsNational"])
            assert np.isnan(r["MktDambullaAvgPrice"])

    def test_lag7_is_the_price_known_seven_days_earlier(self):
        # Lag7 = backward as-of onto (D - 7d). Observation 2025-01-10, Dambulla
        # reported 100 on 2025-01-03 -> Lag7 == 100 (exactly 7 days back).
        result = _frame([("A", "2025-01-10")])
        po = _po([
            ("Dambulla", "A", "2025-01-03", 100.0),
            ("Dambulla", "A", "2025-01-10", 200.0),
        ])
        out = _features._attach_market_spread(result, po, _SLUGS)
        r = out.iloc[0]
        assert r["MktDambullaAvgPrice"] == 200.0
        assert r["MktDambullaLag7"] == 100.0

    def test_leader_market_lag7_tracks_the_currently_highest_market(self):
        # Keppetipola is the highest current market -> LeaderMarketLag7 = its
        # own 7-day-lagged price.
        result = _frame([("A", "2025-01-10")])
        po = _po([
            ("Dambulla", "A", "2025-01-10", 100.0),
            ("Keppetipola", "A", "2025-01-10", 300.0),   # leader
            ("Keppetipola", "A", "2025-01-03", 250.0),   # its lag7
            ("Pettah", "A", "2025-01-10", 110.0),
        ])
        out = _features._attach_market_spread(result, po, _SLUGS)
        r = out.iloc[0]
        assert r["LeaderMarketLag7"] == 250.0

    def test_empty_price_obs_attaches_all_nan_columns(self):
        result = _frame([("A", "2025-01-10")])
        out = _features._attach_market_spread(result, None, _SLUGS)
        r = out.iloc[0]
        assert r["NMarketsReporting"] == 0
        for slug in _SLUGS:
            assert np.isnan(r[f"Mkt{slug}AvgPrice"])
        assert np.isnan(r["SpreadVsNational"])

    def test_output_row_order_and_length_preserved(self):
        # Rows given out of date order must come back aligned to the input order.
        result = _frame([("A", "2025-02-01"), ("A", "2025-01-10"), ("A", "2025-01-20")])
        po = _po([
            ("Dambulla", "A", "2025-01-10", 100.0),
            ("Keppetipola", "A", "2025-01-10", 120.0),
            ("Dambulla", "A", "2025-01-20", 130.0),
            ("Keppetipola", "A", "2025-01-20", 140.0),
        ])
        out = _features._attach_market_spread(result, po, _SLUGS)
        assert len(out) == 3
        assert list(out["ObservationDate"]) == list(result["ObservationDate"])
        # 2025-01-10 row: Dambulla 100 present
        row_0110 = out[out["ObservationDate"] == pd.Timestamp("2025-01-10")].iloc[0]
        assert row_0110["MktDambullaAvgPrice"] == 100.0


class TestMarketSlugHelper:
    def test_slug_takes_leading_word_alnum_only(self):
        assert _load._market_slug("Dambulla Dedicated Economic Centre") == "Dambulla"
        assert _load._market_slug("Pettah (HARTI wholesale)") == "Pettah"
        assert _load._market_slug("Narahenpita (HARTI retail)") == "Narahenpita"

    def test_slug_empty_input_is_empty(self):
        assert _load._market_slug("") == ""
        assert _load._market_slug(None) == ""


# QA leakage/boundary suite (P4 step 2, ClickUp 86caheffr) -- the remaining 6
# tests from the approved spec, appended on top of the builder's 12 unit
# tests above. All hermetic (synthetic frames, no DB) per the spec.

_SPREAD_COLS = [
    "MktDambullaAvgPrice", "MktDambullaLag7",
    "MktKeppetipolaAvgPrice", "MktKeppetipolaLag7",
    "MktThambuttegamaAvgPrice", "MktThambuttegamaLag7",
    "MktPettahAvgPrice", "MktPettahLag7",
    "MktNarahenpitaAvgPrice", "MktNarahenpitaLag7",
    "NMarketsReporting", "SpreadVsNational", "MarketRankPct", "LeaderMarketLag7",
]


class TestPerMarketTruncationInvariance:
    """Leakage-by-truncation, spread-feature flavour (mirrors
    test_phase3.py::TestLeakageByTruncation but hermetic/synthetic). Rebuild
    with all PriceObservations strictly after cutoff T removed; every spread
    column for dates <= T must be BIT-IDENTICAL across both builds."""

    def test_truncated_price_obs_leaves_past_spread_columns_unchanged(self):
        cutoff = pd.Timestamp("2025-02-01")
        result = _frame([
            ("A", "2025-01-10"), ("A", "2025-01-20"),
            ("A", "2025-02-01"), ("A", "2025-02-15"),  # after cutoff
        ])
        po_full = _po([
            ("Dambulla", "A", "2025-01-05", 100.0),
            ("Keppetipola", "A", "2025-01-10", 120.0),
            ("Dambulla", "A", "2025-01-20", 105.0),
            ("Keppetipola", "A", "2025-01-25", 130.0),
            ("Dambulla", "A", "2025-02-10", 999.0),   # future, must not leak back
            ("Keppetipola", "A", "2025-02-14", 888.0),  # future, must not leak back
        ])
        po_trunc = po_full[po_full["ObservedDate"] <= cutoff].copy()

        full = _features._attach_market_spread(result, po_full, _SLUGS)
        trunc = _features._attach_market_spread(result, po_trunc, _SLUGS)

        past_mask = result["ObservationDate"] <= cutoff
        full_past = full.loc[past_mask, _SPREAD_COLS].reset_index(drop=True)
        trunc_past = trunc.loc[past_mask, _SPREAD_COLS].reset_index(drop=True)

        diff = (full_past - trunc_past).abs()
        max_diff = float(np.nanmax(diff.to_numpy())) if diff.size else 0.0
        assert max_diff == 0.0, (
            f"LEAKAGE: spread columns for dates <= cutoff changed when future "
            f"PriceObservations were added/removed. max abs diff = {max_diff:.2e}"
        )
        # Sanity: the future-only rows differ (proves the frames weren't trivially
        # identical, i.e. the truncation actually removed something observable).
        future_mask = ~past_mask
        assert future_mask.any()
        full_future = full.loc[future_mask, "MktDambullaAvgPrice"].reset_index(drop=True)
        trunc_future = trunc.loc[future_mask, "MktDambullaAvgPrice"].reset_index(drop=True)
        assert not full_future.equals(trunc_future) or full_future.isna().all() != trunc_future.isna().all() or (
            full_future.fillna(-1) != trunc_future.fillna(-1)
        ).any(), "expected the future rows to actually differ between full/truncated builds"


class TestNoFutureDatesLeak:
    """No spread cell on date D may change when a PriceObservation dated
    STRICTLY AFTER D is added -- the cardinal as-of rule, applied per-cell."""

    def test_adding_a_future_price_observation_does_not_change_date_d(self):
        d = pd.Timestamp("2025-03-01")
        result = _frame([("A", d.strftime("%Y-%m-%d"))])
        po_before = _po([
            ("Dambulla", "A", "2025-02-25", 100.0),
            ("Keppetipola", "A", "2025-02-20", 110.0),
        ])
        before = _features._attach_market_spread(result, po_before, _SLUGS)

        # Add a PriceObservation dated one day AFTER D with a deliberately
        # extreme "trap" value -- if it leaked in, SpreadVsNational/rank/leader
        # would move.
        po_after = pd.concat([po_before, _po([
            ("Dambulla", "A", "2025-03-02", 999999.0),
            ("Pettah", "A", "2025-03-02", 1.0),
        ])], ignore_index=True)
        after = _features._attach_market_spread(result, po_after, _SLUGS)

        for col in _SPREAD_COLS:
            b, a = before.iloc[0][col], after.iloc[0][col]
            if pd.isna(b) and pd.isna(a):
                continue
            assert b == a, f"{col} changed from {b} to {a} after adding a future-dated row"

    def test_adding_a_same_day_price_observation_is_allowed_to_change_it(self):
        """Companion sanity check: a NEW-market report ON D itself is legitimately
        visible (boundary is inclusive, not exclusive) -- proves the previous
        test isn't vacuously passing because nothing ever changes."""
        d = "2025-03-01"
        result = _frame([("A", d)])
        po_before = _po([("Dambulla", "A", "2025-02-25", 100.0)])
        before = _features._attach_market_spread(result, po_before, _SLUGS)
        assert before.iloc[0]["NMarketsReporting"] == 1

        po_same_day = pd.concat([po_before, _po([
            ("Keppetipola", "A", d, 999.0),
        ])], ignore_index=True)
        after = _features._attach_market_spread(result, po_same_day, _SLUGS)
        assert after.iloc[0]["NMarketsReporting"] == 2, (
            "a report ON the observation date itself must be visible (inclusive boundary)"
        )


class TestRankComputableFromAsOfdColumnsOnly:
    """MarketRankPct must be a pure function of the already-as-of'd per-market
    AvgPrice columns on that row -- recompute it independently from the row
    and assert it matches, proving no side-channel/fresh-query is involved."""

    def test_recomputed_rank_from_row_matches_output_column(self):
        result = _frame([("A", "2025-01-10")])
        po = _po([
            ("Dambulla", "A", "2025-01-10", 100.0),
            ("Keppetipola", "A", "2025-01-10", 120.0),
            ("Thambuttegama", "A", "2025-01-10", 90.0),
            ("Pettah", "A", "2025-01-10", 105.0),
            # Narahenpita absent -> NaN, must be excluded from the recompute too.
        ])
        out = _features._attach_market_spread(result, po, _SLUGS)
        r = out.iloc[0]

        avg_cols = [f"Mkt{slug}AvgPrice" for slug in _SLUGS]
        avg_vals = {c: r[c] for c in avg_cols}
        reporting = {c: v for c, v in avg_vals.items() if pd.notna(v)}
        ref = r["MktDambullaAvgPrice"]

        n_le = sum(1 for v in reporting.values() if v <= ref)
        recomputed_rank = n_le / len(reporting)

        assert r["NMarketsReporting"] == len(reporting)
        assert recomputed_rank == pytest.approx(r["MarketRankPct"])

    @pytest.mark.parametrize("seed", [1, 2, 3, 4])
    def test_recomputed_rank_matches_across_random_reporting_subsets(self, seed):
        """Property-style check over several reporting subsets/prices (still a
        fixed seed -- deterministic, no live RNG state leaks between runs)."""
        rng = np.random.RandomState(seed)
        markets = _SLUGS
        n_reporting = rng.randint(2, len(markets) + 1)
        reporting_slugs = list(rng.choice(markets, size=n_reporting, replace=False))
        prices = {slug: float(rng.randint(50, 200)) for slug in reporting_slugs}

        po = _po([(slug, "A", "2025-01-10", p) for slug, p in prices.items()])
        result = _frame([("A", "2025-01-10")])
        out = _features._attach_market_spread(result, po, _SLUGS)
        r = out.iloc[0]

        if "Dambulla" not in prices:
            assert pd.isna(r["MarketRankPct"])
            return

        ref = prices["Dambulla"]
        n_le = sum(1 for p in prices.values() if p <= ref)
        recomputed_rank = n_le / len(prices)
        assert recomputed_rank == pytest.approx(r["MarketRankPct"])


class TestThinMarketStalenessCapBoundary:
    """Exact-boundary companion to the builder's
    test_staleness_boundary_exactly_14_days_is_kept: pins BOTH sides of the
    cap (14d kept, 15d NaN'd) in one test, and asserts NMarketsReporting
    decrements exactly at the 15d edge."""

    def test_14_days_old_kept_15_days_old_nans_and_decrements_count(self):
        # Observation D = 2025-02-01. One market reports exactly 14d before D
        # (2025-01-18); another reports exactly 15d before D (2025-01-17).
        d = "2025-02-01"
        result = _frame([("A", d)])
        po = _po([
            ("Dambulla", "A", "2025-01-18", 100.0),   # 14d old -> kept
            ("Keppetipola", "A", "2025-01-17", 120.0),  # 15d old -> NaN
            ("Pettah", "A", d, 150.0),  # fresh, always counted
        ])
        out = _features._attach_market_spread(result, po, _SLUGS)
        r = out.iloc[0]

        assert r["MktDambullaAvgPrice"] == 100.0, "14-day-old price must be kept (cap is strict >)"
        assert np.isnan(r["MktKeppetipolaAvgPrice"]), "15-day-old price must be NaN'd"
        # Only Dambulla (14d, kept) + Pettah (fresh) count -> 2, not 3.
        assert r["NMarketsReporting"] == 2

    @pytest.mark.parametrize("age_days,expect_nan", [(13, False), (14, False), (15, True), (30, True)])
    def test_staleness_cap_is_monotone_around_the_14_day_edge(self, age_days, expect_nan):
        d = pd.Timestamp("2025-02-01")
        obs_date = (d - pd.Timedelta(days=age_days)).strftime("%Y-%m-%d")
        result = _frame([("A", d.strftime("%Y-%m-%d"))])
        po = _po([("Dambulla", "A", obs_date, 100.0)])
        out = _features._attach_market_spread(result, po, _SLUGS)
        val = out.iloc[0]["MktDambullaAvgPrice"]
        if expect_nan:
            assert np.isnan(val), f"age={age_days}d should be NaN (cap exceeded)"
        else:
            assert val == 100.0, f"age={age_days}d should still be visible (cap is strict >)"


class TestNaNNotZeroOnReportingGaps:
    """A 2-3 day all-market reporting gap (e.g. around a Poya/holiday) must
    carry the last known price forward (within the 14d cap), NEVER surface a
    0.0, and NMarketsReporting must stay an honest count of markets actually
    within the cap -- not silently padded."""

    @pytest.mark.parametrize("gap_days", [2, 3])
    def test_short_all_market_gap_carries_forward_never_zero(self, gap_days):
        last_report = pd.Timestamp("2025-04-10")
        gap_date = last_report + pd.Timedelta(days=gap_days)
        result = _frame([
            (
                "A",
                (last_report + pd.Timedelta(days=d)).strftime("%Y-%m-%d"),
            )
            for d in range(0, gap_days + 1)
        ])
        po = _po([
            ("Dambulla", "A", last_report.strftime("%Y-%m-%d"), 100.0),
            ("Keppetipola", "A", last_report.strftime("%Y-%m-%d"), 120.0),
        ])
        out = _features._attach_market_spread(result, po, _SLUGS)

        for _, r in out.iterrows():
            # Within the cap (gap_days <= 3 << 14) every row must carry the last
            # known price forward -- never 0.0 -- and count both markets.
            assert r["MktDambullaAvgPrice"] == 100.0
            assert r["MktKeppetipolaAvgPrice"] == 120.0
            assert r["NMarketsReporting"] == 2
            assert r["SpreadVsNational"] != 0.0 or not np.isnan(r["SpreadVsNational"])
            # Explicit not-zero assertions (the actual defect this test guards
            # against: a fillna(0) instead of ffill/NaN discipline).
            assert r["MktDambullaAvgPrice"] != 0.0
            assert r["MktKeppetipolaAvgPrice"] != 0.0

    def test_gap_that_exceeds_cap_yields_nan_not_zero_and_lower_count(self):
        # A 20-day gap (beyond the 14d cap): must be NaN, and NMarketsReporting
        # must drop to reflect only the markets still within the cap.
        last_report = pd.Timestamp("2025-04-10")
        far_date = last_report + pd.Timedelta(days=20)
        result = _frame([("A", far_date.strftime("%Y-%m-%d"))])
        po = _po([
            ("Dambulla", "A", last_report.strftime("%Y-%m-%d"), 100.0),
            ("Keppetipola", "A", far_date.strftime("%Y-%m-%d"), 999.0),  # fresh
        ])
        out = _features._attach_market_spread(result, po, _SLUGS)
        r = out.iloc[0]
        assert np.isnan(r["MktDambullaAvgPrice"]), "beyond-cap gap must be NaN, never carried forever"
        assert r["MktDambullaAvgPrice"] != 0.0  # redundant with isnan but explicit per spec wording
        assert r["MktKeppetipolaAvgPrice"] == 999.0
        assert r["NMarketsReporting"] == 1, "stale market must not inflate the reporting count"


class TestMergeAsofDtypePin:
    """merge_asof dtype-mismatch convention (mirrors test_merge_asof_dtype.py /
    PR #15 invariant), applied to _attach_market_spread / _asof_market_price.

    Repo convention observed in features.py: _canon_key()/_as_canon_dt() pin
    every join key to datetime64[ns] BEFORE the merge, so mismatched raw units
    ([s] vs [us] etc.) silently COERCE to a correct result. A tz-AWARE input,
    however, is a genuinely different dtype family (not just a unit) and
    _canon_key's plain .astype("datetime64[ns]") RAISES TypeError on it rather
    than coercing -- this is the actual repo behaviour today, pinned here so a
    future change (silent coercion, or a different exception type) is a
    visible test change, not a silent behavior drift.
    """

    @pytest.mark.parametrize("obs_unit,po_unit", [("s", "us"), ("us", "s"), ("s", "ns"), ("ns", "us")])
    def test_mismatched_raw_units_coerce_to_ns_and_merge_correctly(self, obs_unit, po_unit):
        result = pd.DataFrame({
            "CropId": ["A"],
            "ObservationDate": pd.to_datetime(["2025-01-10"]).astype(f"datetime64[{obs_unit}]"),
        })
        po = pd.DataFrame({
            "MarketSlug": ["Dambulla"],
            "CropId": ["A"],
            "ObservedDate": pd.to_datetime(["2025-01-05"]).astype(f"datetime64[{po_unit}]"),
            "AvgPrice": [100.0],
        })
        out = _features._attach_market_spread(result, po, ["Dambulla"])
        assert out.iloc[0]["MktDambullaAvgPrice"] == 100.0
        assert out["ObservationDate"].dtype == np.dtype("datetime64[ns]")

    def test_date_typed_input_coerces_via_to_datetime(self):
        """A plain python datetime.date (as opposed to Timestamp/[ns]) input --
        e.g. what a naive DB driver might hand back -- coerces fine because
        pd.to_datetime() upcasts it before the astype pin."""
        import datetime as _dt
        result = pd.DataFrame({
            "CropId": ["A"],
            "ObservationDate": [_dt.date(2025, 1, 10)],
        })
        po = pd.DataFrame({
            "MarketSlug": ["Dambulla"],
            "CropId": ["A"],
            "ObservedDate": [_dt.date(2025, 1, 5)],
            "AvgPrice": [100.0],
        })
        out = _features._attach_market_spread(result, po, ["Dambulla"])
        assert out.iloc[0]["MktDambullaAvgPrice"] == 100.0

    def test_tz_aware_input_raises_typeerror_per_repo_convention(self):
        """tz-aware input is NOT silently coerced today -- _canon_key's
        .astype("datetime64[ns]") raises TypeError on a tz-aware Series. This
        pins the CURRENT behaviour (raise, not silent-wrong-coerce) so a
        regression to silent misalignment would be caught; if the repo later
        decides to support tz-aware inputs by explicit tz_localize(None), this
        test is the one to update alongside that change."""
        result = pd.DataFrame({
            "CropId": ["A"],
            "ObservationDate": pd.to_datetime(["2025-01-10"]).tz_localize("UTC"),
        })
        po = pd.DataFrame({
            "MarketSlug": ["Dambulla"],
            "CropId": ["A"],
            "ObservedDate": pd.to_datetime(["2025-01-05"]),
            "AvgPrice": [100.0],
        })
        with pytest.raises(TypeError):
            _features._attach_market_spread(result, po, ["Dambulla"])
