"""
AgriForecast ML -- festival calendar feature tests (R1.1 P2, ClickUp 86cahefav).

Two tiers:
  PURE (always run, no DB, no network):
    - festival feature helpers in features.py (windows, countdowns, booleans)
    - Avurudu lead-up boundary pins (Apr 12/13/14/15)
    - to_prophet_holidays() reshape
    - no-wall-clock / freeze-time purity
    - historical-vs-as-of parity (same value regardless of when computed)
    - all-festival columns have an explain._LABELS entry
    - REUSABLE generic invariant helpers (P6 will reuse them)
  DB (skipped when the live DB is unreachable):
    - load_festivals() returns the seeded rows with the right shape
    - seed-coverage-vs-price-history: calendar covers every year in the price
      history (the highest-value invariant)
    - all-zero / constant-variance guard on the new columns over the REAL matrix

The reusable helpers (assert_no_wall_clock, assert_calendar_covers_years,
assert_columns_not_constant, assert_as_of_parity) are intentionally generic so
the P6 QA phase can point them at any date-derived feature block.
"""
from __future__ import annotations

import sys
from datetime import date, datetime, timedelta
from pathlib import Path
from unittest import mock

import numpy as np
import pandas as pd
import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import features as F  # noqa: E402
from agriforecast_ml import load as L  # noqa: E402
from agriforecast_ml.serving import explain  # noqa: E402


# ===========================================================================
# Shared fixtures / builders (pure, hermetic)
# ===========================================================================

def _calendar_2019() -> pd.DataFrame:
    """Minimal load_festivals()-shaped frame: the Avurudu Apr-13/14 pair
    (Apr 13 anchors LeadUpDays=14, Apr 14 = 0), Thai Pongal, Christmas."""
    return pd.DataFrame({
        "FestivalKey": ["THAI_PONGAL", "AVURUDU", "AVURUDU", "CHRISTMAS"],
        "Date": pd.to_datetime(["2019-01-14", "2019-04-13", "2019-04-14", "2019-12-25"]),
        "LeadUpDays": [14, 14, 0, 14],
        "IsProvisional": [False, False, False, False],
        "Source": [None, None, None, None],
    })


def _features_for(obs, harv, calendar) -> pd.DataFrame:
    events, windows = F._festival_windows(calendar)
    obs = pd.Series(pd.to_datetime(obs))
    harv = pd.Series(pd.to_datetime(harv))
    return F._festival_features(obs, harv, events, windows)


# ===========================================================================
# GENERIC REUSABLE INVARIANT HELPERS (P6 QA will reuse these)
# ===========================================================================

def assert_calendar_covers_years(covered_years: set[int], required_years: set[int],
                                 what: str = "calendar") -> None:
    """Every year that appears in `required_years` must be covered. This is the
    silent-zero trap: a calendar that stops short zeroes the feature for later
    rows while CV still looks fine."""
    missing = sorted(required_years - covered_years)
    assert not missing, f"{what} is missing years present in the data: {missing}"


def assert_columns_not_constant(df: pd.DataFrame, cols: list[str]) -> None:
    """Guard against the silent-zero / constant-variance trap: over a real
    matrix, a festival column that is all-zero (or single-valued) means the
    feature is dead (bad join, forward-only seed, etc.)."""
    dead = [c for c in cols if df[c].nunique(dropna=False) <= 1]
    assert not dead, f"festival columns are constant over the real matrix: {dead}"


def assert_no_wall_clock(fn, *args, **kwargs):
    """Call `fn` twice under two different frozen 'now's; results must be
    byte-identical. Catches any now()/today()/wall-clock leak in a feature fn."""
    def _run(frozen: datetime):
        real_dt = datetime
        real_date = date

        class _FrozenDateTime(real_dt):
            @classmethod
            def now(cls, tz=None):
                return frozen
            @classmethod
            def utcnow(cls):
                return frozen
            @classmethod
            def today(cls):
                return frozen

        class _FrozenDate(real_date):
            @classmethod
            def today(cls):
                return frozen.date()

        with mock.patch("datetime.datetime", _FrozenDateTime), \
             mock.patch("datetime.date", _FrozenDate):
            # pandas Timestamp.now / Timestamp.today
            with mock.patch.object(pd.Timestamp, "now", classmethod(lambda cls, tz=None: pd.Timestamp(frozen))), \
                 mock.patch.object(pd.Timestamp, "today", classmethod(lambda cls, tz=None: pd.Timestamp(frozen))):
                return fn(*args, **kwargs)

    a = _run(datetime(2001, 1, 1, 3, 0, 0))
    b = _run(datetime(2099, 12, 31, 23, 59, 59))
    pd.testing.assert_frame_equal(a, b)


def assert_as_of_parity(fn, *args, **kwargs):
    """A pure calendar feature must produce the same value no matter WHEN it is
    computed. Alias of the no-wall-clock check phrased as historical-vs-as-of."""
    assert_no_wall_clock(fn, *args, **kwargs)


# ===========================================================================
# 1. Boundary pins for the Avurudu lead-up (the spec's exact pins)
# ===========================================================================

class TestAvuruduBoundaryPins:
    def test_days_to_next_festival_pins(self):
        cal = _calendar_2019()
        f = _features_for(
            ["2019-04-12", "2019-04-13", "2019-04-14", "2019-04-15"],
            ["2019-04-12", "2019-04-13", "2019-04-14", "2019-04-15"],
            cal,
        )
        dd = f["DaysToNextFestivalAny"].tolist()
        # Apr 12 -> Avurudu(Apr 13) is 1 day away
        assert dd[0] == 1.0
        # Apr 13 -> the festival is today -> 0
        assert dd[1] == 0.0
        # Apr 14 -> Avurudu day row is an event today -> 0
        assert dd[2] == 0.0
        # Apr 15 -> next festival is Christmas, far beyond the clip -> clip cap
        assert dd[3] == float(F._FESTIVAL_CLIP_DAYS)

    def test_in_leadup_avurudu_window(self):
        cal = _calendar_2019()
        # Apr 12/13 inside the Apr-13-anchored [Mar 30, Apr 13] window; Apr 15 out.
        f = _features_for(
            ["2019-03-30", "2019-04-12", "2019-04-13", "2019-04-15"],
            ["2019-03-30", "2019-04-12", "2019-04-13", "2019-04-15"],
            cal,
        )
        assert f["InLeadupAvurudu"].tolist() == [1, 1, 1, 0]

    def test_apr14_row_leadupdays_zero_not_double_counted(self):
        """The Apr-14 row carries LeadUpDays=0 (seed convention), so it opens NO
        window of its own -- the window is anchored only on Apr 13."""
        cal = _calendar_2019()
        f = _features_for(["2019-04-14"], ["2019-04-14"], cal)
        # Apr 14 is NOT inside [Mar 30, Apr 13], and its own row has no window.
        assert f["InLeadupAvurudu"].iloc[0] == 0


# ===========================================================================
# 2. Harvest-anchored features (the load-bearing ones)
# ===========================================================================

class TestHarvestAnchored:
    def test_harvest_in_leadup_true_when_harvest_in_window(self):
        cal = _calendar_2019()
        # Observation far from any festival, but HARVEST lands in the Avurudu window.
        f = _features_for(["2019-01-05"], ["2019-04-10"], cal)
        assert f["HarvestInFestivalLeadup"].iloc[0] == 1

    def test_harvest_in_leadup_false_when_harvest_outside(self):
        cal = _calendar_2019()
        f = _features_for(["2019-01-05"], ["2019-06-01"], cal)
        assert f["HarvestInFestivalLeadup"].iloc[0] == 0

    def test_days_from_harvest_to_next_festival_clipped(self):
        cal = _calendar_2019()
        f = _features_for(["2019-01-05", "2019-01-05"], ["2019-04-11", "2019-08-01"], cal)
        # harvest Apr 11 -> next event Apr 13 = 2 days
        assert f["DaysFromHarvestToNextFestival"].iloc[0] == 2.0
        # harvest Aug 1 -> next event Dec 25 is far -> clip cap
        assert f["DaysFromHarvestToNextFestival"].iloc[1] == float(F._FESTIVAL_CLIP_DAYS)

    def test_nat_harvest_degrades_to_far_not_nan(self):
        cal = _calendar_2019()
        f = _features_for(["2019-04-10"], [pd.NaT], cal)
        assert f["HarvestInFestivalLeadup"].iloc[0] == 0
        assert f["DaysFromHarvestToNextFestival"].iloc[0] == float(F._FESTIVAL_CLIP_DAYS)
        assert not np.isnan(f["DaysFromHarvestToNextFestival"].iloc[0])


# ===========================================================================
# 3. Empty-calendar degrade + clip semantics
# ===========================================================================

class TestEmptyCalendarDegrade:
    def test_attach_festivals_empty_calendar_zero_fills(self):
        result = pd.DataFrame({
            "ObservationDate": pd.to_datetime(["2019-04-10", "2019-08-01"]),
            "HarvestDate": pd.to_datetime(["2019-05-01", "2019-09-01"]),
        })
        out = F._attach_festivals(result.copy(), pd.DataFrame(columns=L._FESTIVAL_COLS))
        for col in F.FESTIVAL_FEATURE_COLS:
            assert col in out.columns
        assert (out["HarvestInFestivalLeadup"] == 0).all()
        assert (out["InLeadupAvurudu"] == 0).all()
        # countdowns degrade to the clip cap (no future event known)
        assert (out["DaysToNextFestivalAny"] == float(F._FESTIVAL_CLIP_DAYS)).all()
        assert (out["DaysFromHarvestToNextFestival"] == float(F._FESTIVAL_CLIP_DAYS)).all()

    def test_none_calendar_zero_fills(self):
        result = pd.DataFrame({
            "ObservationDate": pd.to_datetime(["2019-04-10"]),
            "HarvestDate": pd.to_datetime(["2019-05-01"]),
        })
        out = F._attach_festivals(result.copy(), None)
        for col in F.FESTIVAL_FEATURE_COLS:
            assert col in out.columns


# ===========================================================================
# 4. Prophet-readiness
# ===========================================================================

class TestProphetHolidays:
    def test_reshape_columns_and_windows(self):
        cal = _calendar_2019()
        h = L.to_prophet_holidays(cal)
        assert list(h.columns) == ["holiday", "ds", "lower_window", "upper_window"]
        # Prophet effect spans [ds+lower, ds+upper]; ours is [Date-LeadUpDays, Date].
        row13 = h[(h["holiday"] == "AVURUDU") & (h["ds"] == pd.Timestamp("2019-04-13"))].iloc[0]
        assert row13["lower_window"] == -14
        assert row13["upper_window"] == 0

    def test_empty_in_empty_out(self):
        h = L.to_prophet_holidays(pd.DataFrame(columns=L._FESTIVAL_COLS))
        assert list(h.columns) == ["holiday", "ds", "lower_window", "upper_window"]
        assert h.empty


# ===========================================================================
# 5. Purity: no wall-clock, historical-vs-as-of parity
# ===========================================================================

class TestPurity:
    def test_no_wall_clock_in_feature_build(self):
        cal = _calendar_2019()
        obs = pd.Series(pd.to_datetime(["2019-04-01", "2019-08-01", "2019-12-20"]))
        harv = pd.Series(pd.to_datetime(["2019-04-13", "2019-09-15", "2019-12-25"]))
        events, windows = F._festival_windows(cal)
        assert_no_wall_clock(F._festival_features, obs, harv, events, windows)

    def test_as_of_parity(self):
        cal = _calendar_2019()
        obs = pd.Series(pd.to_datetime(["2019-04-10"]))
        harv = pd.Series(pd.to_datetime(["2019-04-13"]))
        events, windows = F._festival_windows(cal)
        assert_as_of_parity(F._festival_features, obs, harv, events, windows)

    def test_old_hardcoded_is_festival_removed(self):
        """The hardcoded _is_festival heuristic must be gone -- only ONE festival
        definition (the DB table) may exist."""
        assert not hasattr(F, "_is_festival")


# ===========================================================================
# 6. explain._LABELS coverage (farmer-facing SHAP names)
# ===========================================================================

class TestExplainLabels:
    def test_every_festival_column_has_a_label(self):
        missing = [c for c in F.FESTIVAL_FEATURE_COLS if c not in explain._LABELS]
        assert not missing, f"festival columns missing an explain._LABELS entry: {missing}"

    def test_is_festival_column_removed_from_producer_label_kept_for_legacy(self):
        """The hardcoded IsFestival column is no longer PRODUCED (features.py),
        but its explain label is deliberately KEPT: the currently-promoted model
        still lists IsFestival in its feature_cols, so dropping the label would
        regress live SHAP to a raw column name. The label retires with the next
        retrain, not with this change."""
        assert not hasattr(F, "_is_festival")
        assert "IsFestival" in explain._LABELS  # legacy served-model compat


# ===========================================================================
# 7. DB-backed invariants (skipped when the live DB is unreachable)
# ===========================================================================

def _db_or_skip():
    try:
        from agriforecast_ml.envfile import load_env_file
        load_env_file()
        f = L.load_festivals()
    except Exception as e:  # pragma: no cover - env-dependent
        pytest.skip(f"DB unreachable: {e}")
    if f.empty:
        pytest.skip("FestivalCalendarEntries empty/unreachable")
    return f


class TestLiveCalendar:
    def test_load_festivals_returns_seed_rows(self):
        f = _db_or_skip()
        assert len(f) == 64, f"expected 64 seed rows, got {len(f)}"
        assert set(f["FestivalKey"].unique()) == {"AVURUDU", "CHRISTMAS", "THAI_PONGAL"}
        assert f["Date"].dtype.kind == "M"
        assert f["IsProvisional"].dtype == bool

    def test_avurudu_pair_leadup_convention(self):
        f = _db_or_skip()
        av = f[f["FestivalKey"] == "AVURUDU"].copy()
        av["md"] = av["Date"].dt.strftime("%m-%d")
        apr13 = av[av["md"] == "04-13"]
        apr14 = av[av["md"] == "04-14"]
        assert (apr13["LeadUpDays"] == 14).all()
        assert (apr14["LeadUpDays"] == 0).all()

    def test_provisional_flag_present_for_future_years(self):
        f = _db_or_skip()
        # 2027-2030 rows are provisional per the seed (do not silently upgrade).
        assert f[f["Date"].dt.year >= 2027]["IsProvisional"].any()

    def test_seed_coverage_vs_price_history(self):
        """HIGHEST-VALUE invariant: the festival calendar must cover every year
        present in the training price history, else festival features silently
        zero for uncovered years."""
        f = _db_or_skip()
        try:
            prices = L.load_prices()
        except Exception as e:  # pragma: no cover
            pytest.skip(f"prices unreachable: {e}")
        price_years = set(pd.to_datetime(prices["PriceDate"]).dt.year.unique())
        cal_years = set(f["Date"].dt.year.unique())
        assert_calendar_covers_years(cal_years, price_years, "festival calendar")

    def test_festival_columns_not_constant_over_real_matrix(self):
        """all-zero / constant-variance guard on the REAL feature matrix."""
        f = _db_or_skip()
        try:
            prices = L.load_prices()
            crops = L.load_crops()
            weather = L.load_weather()
        except Exception as e:  # pragma: no cover
            pytest.skip(f"inputs unreachable: {e}")
        # Build WITHOUT fx/sentiment/policy to isolate festival columns (those
        # national merges are exercised elsewhere; festivals need no as-of merge).
        feats = F.build_all(prices, crops, weather, fx=None, festivals=f)
        assert_columns_not_constant(feats, F.FESTIVAL_FEATURE_COLS)
