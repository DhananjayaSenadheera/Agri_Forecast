"""
AgriForecast ML -- Poya calendar tests (R1.1 P1 Step 5, ClickUp 86cahef64).

All tests are pure-python / hermetic (no DB, no network) -- structural
sanity of the static agriforecast_ml/data/poya_days.py table, plus the
is_poya()/expected_market_closed() lookups in agriforecast_ml/poya.py.

Coverage:
  TestPoyaStructure          -- every year 2015-2030 has 12 or 13 dates,
                                 sorted, no duplicates within a year or
                                 across the whole table; consecutive Poya
                                 dates (across year boundaries too) are
                                 spaced a full-moon-plausible 27-31 days
                                 apart.
  TestIsPoya                 -- known Poya dates resolve True; a plain
                                 non-Poya date resolves False; a date
                                 outside the 2015-2030 covered range
                                 resolves False (fail-open-to-"not Poya",
                                 not a KeyError).
  TestExpectedMarketClosed   -- today's contract is exactly is_poya() --
                                 Sunday is deliberately NOT also treated as
                                 expected-closed (pinned as a regression
                                 test; see poya.py module docstring for the
                                 live-corpus evidence this is based on).
"""
from __future__ import annotations

import sys
from datetime import date, timedelta
from pathlib import Path

import pytest

# Path setup
ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import poya  # noqa: E402
from agriforecast_ml.data.poya_days import POYA_DAYS  # noqa: E402


# 1. Structural sanity of the static table

class TestPoyaStructure:
    def test_covers_every_year_2015_to_2030(self):
        assert set(POYA_DAYS.keys()) == set(range(2015, 2031))

    @pytest.mark.parametrize("year", list(range(2015, 2031)))
    def test_each_year_has_12_or_13_poya_days(self, year):
        dates = POYA_DAYS[year]
        assert len(dates) in (12, 13), (
            f"{year} has {len(dates)} Poya days -- expected 12 (ordinary "
            f"lunar year) or 13 (a year containing an intercalary/'Adhi' "
            f"lunar month)"
        )

    @pytest.mark.parametrize("year", list(range(2015, 2031)))
    def test_each_year_dates_are_sorted_and_within_that_year(self, year):
        dates = [date.fromisoformat(d) for d in POYA_DAYS[year]]
        assert dates == sorted(dates), f"{year} Poya dates are not sorted"
        assert all(d.year == year for d in dates), (
            f"{year} contains a date from a different year"
        )

    def test_no_duplicate_dates_within_a_year(self):
        for year, dates in POYA_DAYS.items():
            assert len(dates) == len(set(dates)), f"{year} has duplicate Poya dates"

    def test_no_duplicate_dates_across_the_whole_table(self):
        all_dates = [d for dates in POYA_DAYS.values() for d in dates]
        assert len(all_dates) == len(set(all_dates)), (
            "A Poya date appears more than once across different years "
            "(should be impossible -- each date belongs to exactly one year)"
        )

    def test_consecutive_poya_dates_are_full_moon_plausible_spacing(self):
        """A lunar (synodic) month is ~29.53 days. Consecutive Poya dates,
        FLATTENED ACROSS THE WHOLE TABLE (i.e. including the December ->
        next-January boundary), must be 27-31 days apart -- loose enough to
        tolerate real calendar variance but tight enough to catch a
        transcription error (wrong month, wrong year, digit typo)."""
        all_dates = sorted(
            date.fromisoformat(d) for dates in POYA_DAYS.values() for d in dates
        )
        bad = []
        for i in range(1, len(all_dates)):
            gap = (all_dates[i] - all_dates[i - 1]).days
            if not (27 <= gap <= 31):
                bad.append((all_dates[i - 1], all_dates[i], gap))
        assert not bad, f"Implausible Poya spacing (expected 27-31 days): {bad}"

    def test_total_date_count_is_in_expected_range(self):
        """16 years * 12-13 dates/year = 192-208 total. A gross data-entry
        error (e.g. an entire year duplicated or omitted) would fall
        outside this range."""
        total = sum(len(dates) for dates in POYA_DAYS.values())
        assert 192 <= total <= 208


# 2. is_poya()

class TestIsPoya:
    def test_known_poya_date_resolves_true(self):
        # 2025-01-13 (Duruthu Poya) is pinned directly from POYA_DAYS.
        assert poya.is_poya(date(2025, 1, 13)) is True

    def test_every_configured_date_resolves_true(self):
        for year, dates in POYA_DAYS.items():
            for d in dates:
                assert poya.is_poya(date.fromisoformat(d)) is True

    def test_plain_non_poya_date_resolves_false(self):
        assert poya.is_poya(date(2025, 1, 1)) is False

    def test_day_adjacent_to_a_poya_date_resolves_false(self):
        """The day immediately before/after a real Poya date must not
        itself be treated as Poya -- catches an off-by-one suppression
        bug in gap_report's usage of this function."""
        poya_date = date(2025, 1, 13)
        assert poya.is_poya(poya_date - timedelta(days=1)) is False
        assert poya.is_poya(poya_date + timedelta(days=1)) is False

    def test_date_outside_covered_range_resolves_false_not_keyerror(self):
        """2010 and 2035 are outside the 2015-2030 table -- must fail open
        to 'not Poya' (so an out-of-range date surfaces as an ordinary gap,
        prompting a calendar extension) rather than raising."""
        assert poya.is_poya(date(2010, 6, 15)) is False
        assert poya.is_poya(date(2035, 6, 15)) is False


# 3. expected_market_closed() -- Poya only, NOT Sunday

class TestExpectedMarketClosed:
    def test_poya_date_is_expected_closed(self):
        assert poya.expected_market_closed(date(2025, 1, 13)) is True

    def test_ordinary_weekday_is_not_expected_closed(self):
        assert poya.expected_market_closed(date(2025, 1, 15)) is False

    def test_sunday_is_not_treated_as_expected_closed(self):
        """Regression pin: 2025-01-12 is a Sunday and NOT in POYA_DAYS[2025]
        (2025-01-13, a Monday, is the Poya date that month). live-corpus
        evidence (see poya.py docstring) showed HARTI does publish on a
        meaningful fraction of Sundays, so Sunday must NOT be suppressed."""
        sunday = date(2025, 1, 12)
        assert sunday.weekday() == 6  # Python: Monday=0 ... Sunday=6
        assert sunday.isoformat() not in POYA_DAYS[2025]
        assert poya.expected_market_closed(sunday) is False

    def test_equals_is_poya_today(self):
        """Pins the current contract explicitly: expected_market_closed is
        exactly is_poya, nothing more, today."""
        for d in [date(2025, 1, 12), date(2025, 1, 13), date(2025, 1, 14), date(2020, 3, 15)]:
            assert poya.expected_market_closed(d) == poya.is_poya(d)
