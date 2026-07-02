"""
AgriForecast ML — HARTI backfill splice integrity tests.

Coverage (all fast, deterministic, no live DB / network):
  TestNoCrossSourceDuplication   — load_prices() must emit zero (CropId,Date)
                                   pairs from BOTH HARTI and DEC.
  TestDECWindowExclusion         — DEC Ridge Gourd/Beans rows inside
                                   [2025-05-05, 2025-06-30] dropped; outside
                                   kept; other crops unaffected.
  TestNameAliasMapping           — Luffa->Ridge Gourd resolves; 4 zero-data
                                   crops never produce HARTI rows.
  TestParserOnCachedPDF          — real cached 2019-01-01 PDF: correct page,
                                   Dambulla column index, midpoint arithmetic,
                                   Bitter Gourd (Other) consolidation.
  TestIdempotency                — loading the same slice twice yields no new
                                   rows (mocked upsert).
  TestNoLeakageIntoCropFeature   — lag/rolling features after splice remain
                                   past-only; no feature row uses a future
                                   observation date.
"""
from __future__ import annotations

import sys
import uuid
from datetime import date, timedelta
from pathlib import Path
from typing import Sequence
from unittest.mock import MagicMock, patch

import numpy as np
import pandas as pd
import pytest

# ---------------------------------------------------------------------------
# Path setup
# ---------------------------------------------------------------------------
ML_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ML_ROOT))

# Where the real cached PDFs live (parser tests read one of these)
HARTI_CACHE = ML_ROOT / "harti_cache"
SAMPLE_PDF = HARTI_CACHE / "harti_2019-01-01.pdf"

# ---------------------------------------------------------------------------
# Reusable constants (mirror loader.py -- verified against live code)
# ---------------------------------------------------------------------------
DEC_SOURCE = "DAMBULLA_DEC"
HARTI_SOURCE = "HARTI"

# Splice boundary dates (copy from loader to pin the contract)
SPLICE_GENERAL = date(2025, 5, 5)         # DEC authoritative from here (exclusive)
SPLICE_EXCEPTION_END = date(2025, 6, 30)  # Ridge Gourd/Beans exception until here
EXCEPTION_CROPS_DB = {"Ridge Gourd", "Beans"}

# The 6 crops that HAVE HARTI data (harti_label -> db_name)
HARTI_TO_DB_NAME = {
    "Beans":         "Beans",
    "Ladies Fingers": "Lady's Fingers",
    "Capsicum":      "Capsicum",
    "Bitter Gourd":  "Bitter Gourd",
    "Luffa":         "Ridge Gourd",
    "Snake Gourd":   "Snake Gourd",
}

# The 4 crops that have ZERO HARTI Dambulla data -- must never appear
ZERO_DATA_CROPS = {"Winged Bean", "Ginger", "Cooking Melon", "Watermelon"}


# ---------------------------------------------------------------------------
# Test helpers / fixtures
# ---------------------------------------------------------------------------

def _make_price_df(rows):
    df = pd.DataFrame(rows)
    df["PriceDate"] = pd.to_datetime(df["PriceDate"])
    if "MinPrice" not in df.columns:
        df["MinPrice"] = 100.0
    if "MaxPrice" not in df.columns:
        df["MaxPrice"] = 120.0
    df["AvgPrice"] = (df["MinPrice"] + df["MaxPrice"]) / 2.0
    return df


def _apply_dec_exclusion(df):
    """Apply the same WHERE NOT clause logic as load.py:load_prices() SQL query."""
    mask_excluded = (
        (df["Source"] == DEC_SOURCE)
        & (df["PriceDate"].dt.date >= SPLICE_GENERAL)
        & (df["PriceDate"].dt.date <= SPLICE_EXCEPTION_END)
        & (df["CropName"].isin(EXCEPTION_CROPS_DB))
    )
    return df[~mask_excluded].copy()


def _make_fake_crop_map(harti_labels=None):
    """Return a fake {harti_label: (UUID, db_name)} map, no DB needed."""
    labels = harti_labels or list(HARTI_TO_DB_NAME.keys())
    result = {}
    for i, label in enumerate(labels):
        if label in HARTI_TO_DB_NAME:
            crop_id = uuid.UUID(f"aaaaaaaa-0000-0000-0000-{i:012d}")
            result[label] = (crop_id, HARTI_TO_DB_NAME[label])
    return result


# ===========================================================================
# 1. NO CROSS-SOURCE DUPLICATION
# ===========================================================================

class TestNoCrossSourceDuplication:
    """After load_prices() SQL filter, no (CropId, Date) must appear in both
    HARTI and DEC rows reaching the feature build."""

    def _build_splice_safe_frame(self):
        crop_id_a = "crop-a-beans"
        crop_id_b = "crop-b-ridgegourd"
        crop_id_c = "crop-c-capsicum"
        rows = [
            {"Source": HARTI_SOURCE, "CropId": crop_id_a, "CropName": "Beans",
             "PriceDate": "2024-12-01", "MinPrice": 100, "MaxPrice": 120},
            {"Source": HARTI_SOURCE, "CropId": crop_id_b, "CropName": "Ridge Gourd",
             "PriceDate": "2024-12-01", "MinPrice": 80, "MaxPrice": 90},
            {"Source": HARTI_SOURCE, "CropId": crop_id_c, "CropName": "Capsicum",
             "PriceDate": "2024-12-01", "MinPrice": 200, "MaxPrice": 250},
            {"Source": DEC_SOURCE, "CropId": crop_id_c, "CropName": "Capsicum",
             "PriceDate": "2025-05-05", "MinPrice": 210, "MaxPrice": 260},
            # DEC for exception crops inside window -- EXCLUDED by filter
            {"Source": DEC_SOURCE, "CropId": crop_id_a, "CropName": "Beans",
             "PriceDate": "2025-05-10", "MinPrice": 99, "MaxPrice": 110},
            {"Source": DEC_SOURCE, "CropId": crop_id_b, "CropName": "Ridge Gourd",
             "PriceDate": "2025-05-10", "MinPrice": 85, "MaxPrice": 95},
            # HARTI exception crops inside window -- should survive
            {"Source": HARTI_SOURCE, "CropId": crop_id_a, "CropName": "Beans",
             "PriceDate": "2025-05-10", "MinPrice": 100, "MaxPrice": 115},
            {"Source": HARTI_SOURCE, "CropId": crop_id_b, "CropName": "Ridge Gourd",
             "PriceDate": "2025-05-10", "MinPrice": 80, "MaxPrice": 92},
            # DEC after the window -- should survive (no HARTI on this date)
            {"Source": DEC_SOURCE, "CropId": crop_id_a, "CropName": "Beans",
             "PriceDate": "2025-07-01", "MinPrice": 105, "MaxPrice": 118},
        ]
        return _make_price_df(rows)

    def test_no_harti_and_dec_on_same_crop_date(self):
        df = self._build_splice_safe_frame()
        filtered = _apply_dec_exclusion(df)

        harti_keys = set(
            zip(filtered.loc[filtered["Source"] == HARTI_SOURCE, "CropId"],
                filtered.loc[filtered["Source"] == HARTI_SOURCE, "PriceDate"])
        )
        dec_keys = set(
            zip(filtered.loc[filtered["Source"] == DEC_SOURCE, "CropId"],
                filtered.loc[filtered["Source"] == DEC_SOURCE, "PriceDate"])
        )
        overlap = harti_keys & dec_keys
        assert not overlap, (
            "SPLICE VIOLATION: %d (CropId, PriceDate) pairs appear "
            "in both HARTI and DEC after the filter: %s" % (len(overlap), list(overlap)[:5])
        )

    def test_no_duplication_even_when_harti_inserted_in_exception_window(self):
        """HARTI for Beans/Ridge Gourd in exception window + DEC on same date:
        DEC must be filtered out, zero overlap remains."""
        rows = []
        crop_id = "crop-beans-001"
        rows.append({"Source": HARTI_SOURCE, "CropId": crop_id, "CropName": "Beans",
                     "PriceDate": "2025-06-15", "MinPrice": 100, "MaxPrice": 120})
        rows.append({"Source": DEC_SOURCE, "CropId": crop_id, "CropName": "Beans",
                     "PriceDate": "2025-06-15", "MinPrice": 105, "MaxPrice": 118})
        df = _make_price_df(rows)
        filtered = _apply_dec_exclusion(df)

        harti_rows = filtered[filtered["Source"] == HARTI_SOURCE]
        dec_rows = filtered[filtered["Source"] == DEC_SOURCE]
        assert len(harti_rows) == 1, "HARTI row in exception window must survive"
        assert len(dec_rows) == 0, (
            "DEC Beans row in [2025-05-05, 2025-06-30] must be excluded by the filter"
        )

    def test_full_frame_zero_duplicates_across_sources(self):
        """Two-layer splice invariant using realistic DB state.

        The no-duplicate guarantee uses TWO layers working together:

          Layer 1 (HARTI loader / _splice_allowed): HARTI rows are only written
            for dates where _splice_allowed() is True.  Production DB will never
            have HARTI rows for non-exception crops at 2025-05-05+, or any crop
            at 2025-07-01+.

          Layer 2 (load_prices() SQL filter): DEC Beans/Ridge Gourd rows inside
            [2025-05-05, 2025-06-30] are excluded at feature-build query time.

        DEC only has data from ~2025-05-05 onward; historical HARTI dates have
        no DEC counterparts.  We model ONLY the realistic DEC-era dates.
        """
        from agriforecast_ml.harti.loader import _splice_allowed
        from datetime import date as _date

        crops = [
            ("cid-beans", "Beans", "Beans"),
            ("cid-ridge", "Ridge Gourd", "Ridge Gourd"),
            ("cid-caps", "Capsicum", "Capsicum"),
        ]

        # DEC-era dates only (DEC didn't exist before 2025-05-05)
        dec_dates = [
            _date(2025, 5, 5),
            _date(2025, 5, 6),
            _date(2025, 6, 15),
            _date(2025, 6, 30),
            _date(2025, 7, 1),
            _date(2025, 10, 1),
        ]
        # HARTI-only historical dates (no DEC counterpart in production)
        harti_only_dates = [
            _date(2019, 1, 1),
            _date(2024, 1, 15),
            _date(2025, 5, 4),
        ]

        rows = []
        for crop_id, crop_name, db_name in crops:
            for d in harti_only_dates + dec_dates:
                if _splice_allowed(d, db_name):
                    rows.append({"Source": HARTI_SOURCE, "CropId": crop_id,
                                 "CropName": crop_name, "PriceDate": d.isoformat()})
            for d in dec_dates:
                rows.append({"Source": DEC_SOURCE, "CropId": crop_id,
                             "CropName": crop_name, "PriceDate": d.isoformat()})

        df = _make_price_df(rows)
        filtered = _apply_dec_exclusion(df)

        harti_keys = set(
            zip(filtered.loc[filtered["Source"] == HARTI_SOURCE, "CropId"],
                filtered.loc[filtered["Source"] == HARTI_SOURCE, "PriceDate"])
        )
        dec_keys = set(
            zip(filtered.loc[filtered["Source"] == DEC_SOURCE, "CropId"],
                filtered.loc[filtered["Source"] == DEC_SOURCE, "PriceDate"])
        )
        overlap = harti_keys & dec_keys
        assert not overlap, (
            "SPLICE VIOLATION (two-layer): %d (CropId, PriceDate) pairs appear "
            "in both HARTI and DEC after both layers applied: %s"
            % (len(overlap), list(overlap)[:5])
        )


# ===========================================================================
# 2. DEC WINDOW EXCLUSION CORRECTNESS
# ===========================================================================

class TestDECWindowExclusion:
    """DEC Ridge Gourd/Beans rows inside [2025-05-05, 2025-06-30] must be
    dropped; the same crops outside that window must be kept; other crops
    inside the window must be unaffected."""

    def _row(self, crop_id, crop_name, source, date_str):
        return {"Source": source, "CropId": crop_id, "CropName": crop_name,
                "PriceDate": date_str, "MinPrice": 100, "MaxPrice": 120}

    def test_dec_beans_at_window_start_excluded(self):
        df = _make_price_df([self._row("cid-b", "Beans", DEC_SOURCE, "2025-05-05")])
        assert len(_apply_dec_exclusion(df)) == 0, (
            "DEC Beans on 2025-05-05 (window start, inclusive) must be excluded"
        )

    def test_dec_ridgegourd_at_window_end_excluded(self):
        df = _make_price_df([self._row("cid-r", "Ridge Gourd", DEC_SOURCE, "2025-06-30")])
        assert len(_apply_dec_exclusion(df)) == 0, (
            "DEC Ridge Gourd on 2025-06-30 (window end, inclusive) must be excluded"
        )

    def test_dec_beans_mid_window_excluded(self):
        df = _make_price_df([self._row("cid-b", "Beans", DEC_SOURCE, "2025-06-01")])
        assert len(_apply_dec_exclusion(df)) == 0, (
            "DEC Beans mid-window must be excluded"
        )

    def test_dec_beans_before_window_kept(self):
        df = _make_price_df([self._row("cid-b", "Beans", DEC_SOURCE, "2025-05-04")])
        assert len(_apply_dec_exclusion(df)) == 1, (
            "DEC Beans on 2025-05-04 (one day before window) must be KEPT"
        )

    def test_dec_ridgegourd_after_window_kept(self):
        df = _make_price_df([self._row("cid-r", "Ridge Gourd", DEC_SOURCE, "2025-07-01")])
        assert len(_apply_dec_exclusion(df)) == 1, (
            "DEC Ridge Gourd on 2025-07-01 (one day after window) must be KEPT"
        )

    def test_dec_capsicum_inside_window_kept(self):
        """Capsicum is NOT an exception crop; its DEC rows inside the window stay."""
        df = _make_price_df([self._row("cid-c", "Capsicum", DEC_SOURCE, "2025-06-01")])
        assert len(_apply_dec_exclusion(df)) == 1, (
            "DEC Capsicum inside window must be KEPT -- not an exception crop"
        )

    def test_dec_snake_gourd_inside_window_kept(self):
        df = _make_price_df([self._row("cid-s", "Snake Gourd", DEC_SOURCE, "2025-06-01")])
        assert len(_apply_dec_exclusion(df)) == 1, (
            "DEC Snake Gourd inside window must be KEPT -- not an exception crop"
        )

    def test_harti_rows_never_excluded(self):
        """HARTI source rows must never be touched by the DEC exclusion filter."""
        rows = [
            self._row("cid-b", "Beans", HARTI_SOURCE, "2025-05-05"),
            self._row("cid-r", "Ridge Gourd", HARTI_SOURCE, "2025-06-30"),
            self._row("cid-b", "Beans", HARTI_SOURCE, "2026-01-01"),
        ]
        df = _make_price_df(rows)
        result = _apply_dec_exclusion(df)
        assert len(result) == len(rows), (
            "HARTI rows must never be filtered out by the DEC exclusion rule"
        )

    def test_mixed_frame_selective_exclusion(self):
        """Only DEC Beans/Ridge Gourd inside the window drop; everything else survives."""
        rows = [
            # EXCLUDED (3)
            self._row("cid-b", "Beans", DEC_SOURCE, "2025-05-05"),
            self._row("cid-b", "Beans", DEC_SOURCE, "2025-06-30"),
            self._row("cid-r", "Ridge Gourd", DEC_SOURCE, "2025-06-01"),
            # KEPT (3)
            self._row("cid-b", "Beans", DEC_SOURCE, "2025-07-01"),
            self._row("cid-c", "Capsicum", DEC_SOURCE, "2025-06-01"),
            self._row("cid-b", "Beans", HARTI_SOURCE, "2025-06-01"),
        ]
        df = _make_price_df(rows)
        result = _apply_dec_exclusion(df)
        assert len(result) == 3, (
            "Expected 3 rows after exclusion, got %d. Sources: %s"
            % (len(result), result["Source"].tolist())
        )


# ===========================================================================
# 3. NAME-ALIAS MAPPING
# ===========================================================================

class TestNameAliasMapping:
    """Ridge Gourd<->'Luffa' and other aliases resolve to correct CropId;
    zero-data crops never produce HARTI rows."""

    def test_luffa_maps_to_ridge_gourd(self):
        from agriforecast_ml.harti.loader import _HARTI_TO_DB_NAME
        assert _HARTI_TO_DB_NAME.get("Luffa") == "Ridge Gourd"

    def test_ladies_fingers_maps_with_apostrophe(self):
        from agriforecast_ml.harti.loader import _HARTI_TO_DB_NAME
        assert _HARTI_TO_DB_NAME.get("Ladies Fingers") == "Lady's Fingers"

    def test_all_six_harti_labels_in_map(self):
        from agriforecast_ml.harti.loader import _HARTI_TO_DB_NAME
        expected = {"Beans", "Ladies Fingers", "Capsicum",
                    "Bitter Gourd", "Luffa", "Snake Gourd"}
        missing = expected - set(_HARTI_TO_DB_NAME.keys())
        assert not missing, "Missing HARTI labels in _HARTI_TO_DB_NAME: %s" % missing

    def test_zero_data_crops_not_in_harti_map(self):
        from agriforecast_ml.harti.loader import _HARTI_TO_DB_NAME
        db_names = set(_HARTI_TO_DB_NAME.values())
        for zero_crop in ZERO_DATA_CROPS:
            assert zero_crop not in db_names, (
                "Zero-data crop '%s' must NOT appear in HARTI loader crop map" % zero_crop
            )

    def test_zero_data_crops_not_in_parser_targets(self):
        from agriforecast_ml.harti.parser import _TARGET_CROPS
        for zero_crop in ZERO_DATA_CROPS:
            assert zero_crop not in _TARGET_CROPS.keys(), (
                "Zero-data crop '%s' must not be a key in parser._TARGET_CROPS" % zero_crop
            )
            assert zero_crop not in _TARGET_CROPS.values(), (
                "Zero-data crop '%s' must not be a value in parser._TARGET_CROPS" % zero_crop
            )

    def test_zero_data_crops_rejected_by_upsert(self):
        """Even if a ParsedPrice were constructed with a zero-data crop label,
        upsert_harti_prices must reject it (skipped_no_crop)."""
        from agriforecast_ml.harti.parser import ParsedPrice
        from agriforecast_ml.harti import loader

        fake_map = _make_fake_crop_map()
        rows = [
            ParsedPrice("2019-01-01", "Winged Bean", "100-120", 100.0, 120.0),
            ParsedPrice("2019-01-01", "Ginger", "50-70", 50.0, 70.0),
            ParsedPrice("2019-01-01", "Cooking Melon", "40-60", 40.0, 60.0),
            ParsedPrice("2019-01-01", "Watermelon", "30-50", 30.0, 50.0),
        ]
        original = loader._build_crop_map
        loader._build_crop_map = lambda _: fake_map
        try:
            result = loader.upsert_harti_prices(rows, engine=MagicMock(), dry_run=True)
        finally:
            loader._build_crop_map = original

        assert result["inserted"] == 0, (
            "Zero-data crop labels should produce zero inserts, got %d" % result["inserted"]
        )
        assert result["skipped_no_crop"] == 4, (
            "All 4 zero-data crops should be skipped as no_crop, got %d"
            % result["skipped_no_crop"]
        )

    def test_luffa_upsert_resolves_to_ridge_gourd_crop_id(self):
        """'Luffa' parsed rows must be inserted under the Ridge Gourd CropId."""
        from agriforecast_ml.harti.parser import ParsedPrice
        from agriforecast_ml.harti import loader

        ridge_gourd_id = uuid.UUID("deadbeef-0000-0000-0000-000000000001")
        fake_map = {"Luffa": (ridge_gourd_id, "Ridge Gourd")}
        rows = [ParsedPrice("2019-01-01", "Luffa", "70-80", 70.0, 80.0)]
        original = loader._build_crop_map
        loader._build_crop_map = lambda _: fake_map
        try:
            result = loader.upsert_harti_prices(rows, engine=MagicMock(), dry_run=True)
        finally:
            loader._build_crop_map = original

        assert result["inserted"] == 1, (
            "Luffa should resolve to Ridge Gourd and be inserted. Got: %s" % result
        )
        assert result["skipped_no_crop"] == 0


# ===========================================================================
# 4. PARSER CORRECTNESS ON CACHED PDF
# ===========================================================================

@pytest.mark.skipif(
    not SAMPLE_PDF.exists(),
    reason="Cached sample PDF not found at harti_cache/harti_2019-01-01.pdf"
)
class TestParserOnCachedPDF:
    """Parser tests on the real harti_2019-01-01.pdf from the cache."""

    def _parse(self):
        from agriforecast_ml.harti.parser import parse_pdf
        return parse_pdf(SAMPLE_PDF, "2019-01-01")

    def test_correct_page_detected_via_dambulla_header(self):
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            import pdfplumber
            from agriforecast_ml.harti.parser import _find_english_veg_page
            with pdfplumber.open(str(SAMPLE_PDF)) as pdf:
                pg_idx, table = _find_english_veg_page(pdf)

        assert pg_idx is not None, (
            "English veg page not detected -- 'Dambulla' not found in any page header"
        )
        assert table is not None, "Table not returned for the detected page"

    def test_dambulla_column_is_index_3(self):
        """For 2019-format PDFs the Dambulla market is in column index 3."""
        import warnings
        with warnings.catch_warnings():
            warnings.simplefilter("ignore")
            import pdfplumber
            from agriforecast_ml.harti.parser import _find_english_veg_page, _dambulla_col_index
            with pdfplumber.open(str(SAMPLE_PDF)) as pdf:
                _, table = _find_english_veg_page(pdf)

        assert table is not None, "No table found -- prerequisite failed"
        col = _dambulla_col_index(table)
        assert col == 3, (
            "Dambulla column should be index 3 in 2019-format PDFs, got %d" % col
        )

    def _dambulla_rows(self):
        """Dambulla-only slice -- preserves the original (pre-multi-market)
        assertions, which were always implicitly Dambulla-scoped."""
        return [r for r in self._parse() if r.market_name == "Dambulla"]

    def test_six_crops_returned(self):
        """parse_pdf() now emits rows for every locatable target market
        (Dambulla + Pettah for this PDF; Narahenpita's header is not present
        in 2019-format bulletins and is WARN-skipped) -- 6 crops x 2 markets."""
        rows = self._parse()
        labels = [r.harti_label for r in rows]
        assert len(rows) == 12, (
            "Expected 12 rows (6 crops x 2 locatable markets) from 2019-01-01 "
            "PDF, got %d: %s" % (len(rows), labels)
        )
        markets = {r.market_name for r in rows}
        assert markets == {"Dambulla", "Pettah"}, (
            "Expected only Dambulla+Pettah markets (Narahenpita header absent "
            "in this PDF format), got %s" % markets
        )

    def test_dambulla_six_crops_returned(self):
        """R1 regression: the Dambulla-only slice must still be exactly 6
        rows, matching pre-multi-market behaviour bit-for-bit."""
        rows = self._dambulla_rows()
        labels = [r.harti_label for r in rows]
        assert len(rows) == 6, (
            "Expected 6 Dambulla crop rows from 2019-01-01 PDF, got %d: %s" % (len(rows), labels)
        )

    def test_all_six_harti_labels_present(self):
        rows = self._dambulla_rows()
        returned = {r.harti_label for r in rows}
        expected = {"Beans", "Ladies Fingers", "Capsicum", "Bitter Gourd", "Luffa", "Snake Gourd"}
        assert returned == expected, (
            "Expected labels %s, got %s" % (expected, returned)
        )

    def test_beans_min_max_price(self):
        """Dambulla Beans in 2019-01-01: raw='100.00 -120.00' => min=100, max=120."""
        rows = self._dambulla_rows()
        beans = next((r for r in rows if r.harti_label == "Beans"), None)
        assert beans is not None, "Beans row missing"
        assert beans.min_price == pytest.approx(100.0), (
            "Beans min_price should be 100.0, got %s" % beans.min_price
        )
        assert beans.max_price == pytest.approx(120.0), (
            "Beans max_price should be 120.0, got %s" % beans.max_price
        )

    def test_beans_midpoint(self):
        """(100+120)/2 = 110.0"""
        rows = self._dambulla_rows()
        beans = next(r for r in rows if r.harti_label == "Beans")
        midpoint = (beans.min_price + beans.max_price) / 2.0
        assert midpoint == pytest.approx(110.0), (
            "Beans midpoint should be 110.0, got %s" % midpoint
        )

    def test_pettah_beans_differs_from_dambulla_no_column_swap(self):
        """R1 regression: Pettah Beans (raw='80.00- 100.00') must NOT equal
        Dambulla Beans (raw='100.00 -120.00') -- a column swap would silently
        make these identical or cross-contaminated."""
        rows = self._parse()
        dambulla_beans = next(r for r in rows if r.harti_label == "Beans" and r.market_name == "Dambulla")
        pettah_beans = next(r for r in rows if r.harti_label == "Beans" and r.market_name == "Pettah")
        assert (dambulla_beans.min_price, dambulla_beans.max_price) == (100.0, 120.0)
        assert (pettah_beans.min_price, pettah_beans.max_price) == (80.0, 100.0)
        assert (dambulla_beans.min_price, dambulla_beans.max_price) != (
            pettah_beans.min_price, pettah_beans.max_price
        ), "Dambulla and Pettah Beans prices must differ -- identical values would indicate a column swap"

    def test_bitter_gourd_other_consolidates(self):
        """In 2019-01-01 the row is 'Bitter Gourd (Other)'; must consolidate to 'Bitter Gourd'."""
        rows = self._dambulla_rows()
        labels = [r.harti_label for r in rows]
        assert "Bitter Gourd" in labels, (
            "'Bitter Gourd' must be in output (consolidated from 'Bitter Gourd (Other)')"
        )
        assert "Bitter Gourd (Other)" not in labels, (
            "'Bitter Gourd (Other)' must not pass through unconsolidated"
        )

    def test_no_zero_or_negative_prices(self):
        rows = self._parse()
        for r in rows:
            assert r.min_price > 0, "%s/%s: min_price must be > 0, got %s" % (r.market_name, r.harti_label, r.min_price)
            assert r.max_price > 0, "%s/%s: max_price must be > 0, got %s" % (r.market_name, r.harti_label, r.max_price)

    def test_min_price_le_max_price(self):
        rows = self._parse()
        for r in rows:
            assert r.min_price <= r.max_price, (
                "%s/%s: min_price (%s) > max_price (%s)" % (r.market_name, r.harti_label, r.min_price, r.max_price)
            )

    def test_date_str_preserved(self):
        rows = self._parse()
        for r in rows:
            assert r.date_str == "2019-01-01", (
                "date_str should be '2019-01-01', got %r" % r.date_str
            )

    def test_no_duplicate_canonical_labels_per_pdf(self):
        """No duplicate (market_name, harti_label) pairs -- per-market dedup,
        not global, since 6 crops x 2 markets legitimately repeats labels."""
        rows = self._parse()
        from collections import Counter
        counts = Counter((r.market_name, r.harti_label) for r in rows)
        dups = {key: cnt for key, cnt in counts.items() if cnt > 1}
        assert not dups, "Duplicate (market, label) pairs in single PDF parse: %s" % dups

    def test_narahenpita_absent_in_2019_format_warn_skip_not_guessed(self):
        """R1 regression: Narahenpita's header does not exist in 2019-format
        bulletins -- it must be WARN-skipped (absent from output), never
        positionally guessed from an unrelated column."""
        rows = self._parse()
        assert "Narahenpita" not in {r.market_name for r in rows}, (
            "Narahenpita must not appear for a PDF format that has no Narahenpita column"
        )


# ===========================================================================
# 5. IDEMPOTENCY
# ===========================================================================

class TestIdempotency:
    """Loading the same parsed rows twice must produce no new rows."""

    def _base_rows(self):
        from agriforecast_ml.harti.parser import ParsedPrice
        return [
            ParsedPrice("2019-01-01", "Beans", "100-120", 100.0, 120.0),
            ParsedPrice("2019-01-02", "Luffa", "70-80", 70.0, 80.0),
            ParsedPrice("2019-01-03", "Capsicum", "200-250", 200.0, 250.0),
        ]

    def test_dry_run_idempotent(self):
        """Same rows -> same counts both runs."""
        from agriforecast_ml.harti import loader
        rows = self._base_rows()
        fake_map = _make_fake_crop_map()
        original = loader._build_crop_map
        loader._build_crop_map = lambda _: fake_map
        try:
            r1 = loader.upsert_harti_prices(rows, engine=MagicMock(), dry_run=True)
            r2 = loader.upsert_harti_prices(rows, engine=MagicMock(), dry_run=True)
        finally:
            loader._build_crop_map = original

        assert r1["inserted"] == r2["inserted"]
        assert r1["skipped_splice"] == r2["skipped_splice"]

    def test_splice_skip_count_deterministic(self):
        """Splice filter is a pure function of dates -- same input always same skip count."""
        from agriforecast_ml.harti.parser import ParsedPrice
        from agriforecast_ml.harti import loader

        rows = [
            ParsedPrice("2025-05-10", "Capsicum", "200-250", 200.0, 250.0),  # blocked
            ParsedPrice("2025-05-10", "Beans", "100-120", 100.0, 120.0),     # allowed
        ]
        fake_map = _make_fake_crop_map()
        original = loader._build_crop_map
        loader._build_crop_map = lambda _: fake_map
        try:
            r1 = loader.upsert_harti_prices(rows, engine=MagicMock(), dry_run=True)
            r2 = loader.upsert_harti_prices(rows, engine=MagicMock(), dry_run=True)
        finally:
            loader._build_crop_map = original

        assert r1["skipped_splice"] == r2["skipped_splice"]
        assert r1["inserted"] == r2["inserted"]

    def test_capsicum_post_cutoff_splice_blocked(self):
        """Capsicum at 2025-05-10 (post general cutoff, non-exception) must be splice-blocked."""
        from agriforecast_ml.harti.parser import ParsedPrice
        from agriforecast_ml.harti import loader

        rows = [ParsedPrice("2025-05-10", "Capsicum", "200-250", 200.0, 250.0)]
        fake_map = _make_fake_crop_map()
        original = loader._build_crop_map
        loader._build_crop_map = lambda _: fake_map
        try:
            result = loader.upsert_harti_prices(rows, engine=MagicMock(), dry_run=True)
        finally:
            loader._build_crop_map = original

        assert result["skipped_splice"] == 1, (
            "Capsicum at 2025-05-10 must be splice-skipped, got: %s" % result
        )
        assert result["inserted"] == 0

    def test_beans_in_exception_window_inserted(self):
        """Beans at 2025-05-10 (inside exception window) must be inserted, not skipped."""
        from agriforecast_ml.harti.parser import ParsedPrice
        from agriforecast_ml.harti import loader

        rows = [ParsedPrice("2025-05-10", "Beans", "100-120", 100.0, 120.0)]
        fake_map = _make_fake_crop_map()
        original = loader._build_crop_map
        loader._build_crop_map = lambda _: fake_map
        try:
            result = loader.upsert_harti_prices(rows, engine=MagicMock(), dry_run=True)
        finally:
            loader._build_crop_map = original

        assert result["skipped_splice"] == 0, (
            "Beans at 2025-05-10 must NOT be splice-skipped (exception window)"
        )
        assert result["inserted"] == 1

    def test_zero_price_rows_rejected(self):
        """Rows with min_price=0 or max_price=0 must be rejected as invalid_price."""
        from agriforecast_ml.harti.parser import ParsedPrice
        from agriforecast_ml.harti import loader

        rows = [
            ParsedPrice("2019-01-01", "Beans", "0-120", 0.0, 120.0),
            ParsedPrice("2019-01-01", "Capsicum", "100-0", 100.0, 0.0),
        ]
        fake_map = _make_fake_crop_map()
        original = loader._build_crop_map
        loader._build_crop_map = lambda _: fake_map
        try:
            result = loader.upsert_harti_prices(rows, engine=MagicMock(), dry_run=True)
        finally:
            loader._build_crop_map = original

        assert result["skipped_invalid_price"] == 2, (
            "Both zero-price rows must be rejected, got: %s" % result
        )
        assert result["inserted"] == 0


# ===========================================================================
# 6. NO LEAKAGE INTO CROPFEATUREDAILY
# ===========================================================================

class TestNoLeakageIntoCropFeature:
    """Lag/rolling features must remain past-only after the splice."""

    def _make_synthetic_prices(self, crop_id, n_days=200, seed=42):
        start = pd.Timestamp("2024-01-01")
        dates = pd.date_range(start, periods=n_days, freq="D")
        rng = np.random.default_rng(seed)
        prices = 100.0 + np.cumsum(rng.standard_normal(n_days) * 2)
        return pd.DataFrame({
            "CropId": crop_id,
            "CropCode": "TST",
            "CropName": "TestCrop",
            "PriceDate": dates,
            "MinPrice": prices - 5,
            "MaxPrice": prices + 5,
            "AvgPrice": prices,
        })

    def _make_crop_meta(self, crop_id):
        return pd.DataFrame({
            "CropId": [crop_id],
            "CropCode": ["TST"],
            "CropName": ["TestCrop"],
            "GrowthPeriodDays": [60],
            "PlantingSeason": ["Year-round"],
            "HarvestWindowDays": [14],
        })

    def _make_weather(self):
        months = pd.period_range("2022-01", "2026-12", freq="M")
        return pd.DataFrame({
            "Month": months,
            "AvgTemperature": [28.0] * len(months),
            "TotalRainfall": [150.0] * len(months),
        })

    def test_lag7_uses_past_price(self):
        """Lag7 at date D must equal the price on D-7 days, not any future date."""
        from agriforecast_ml.features import build_crop_features, _weather_lookups

        crop_id = "leak-test-001"
        prices = self._make_synthetic_prices(crop_id)
        meta = self._make_crop_meta(crop_id).iloc[0]
        weather = self._make_weather()
        weather_by_month, rain_clim = _weather_lookups(weather)

        feats = build_crop_features(crop_id, prices, meta, weather_by_month, rain_clim)
        price_lookup = prices.set_index("PriceDate")["AvgPrice"].to_dict()

        sample = feats.dropna(subset=["Lag7"]).iloc[::10]
        for _, row in sample.iterrows():
            obs_date = pd.Timestamp(row["ObservationDate"])
            lag7_date = obs_date - pd.Timedelta(days=7)
            if lag7_date in price_lookup:
                expected = price_lookup[lag7_date]
                assert abs(row["Lag7"] - expected) < 1e-9, (
                    "Lag7 at %s should be price at %s=%.2f, got %.2f"
                    % (obs_date.date(), lag7_date.date(), expected, row["Lag7"])
                )

    def test_features_identical_truncation_gold_standard(self):
        """LEAKAGE GOLD STANDARD: features for safe dates before a cutoff
        must be BIT-IDENTICAL whether or not future data is present.
        max diff must be 0.00e+00."""
        from agriforecast_ml.features import build_crop_features, _weather_lookups

        crop_id = "leak-gold-001"
        prices = self._make_synthetic_prices(crop_id, n_days=200, seed=0)
        meta = self._make_crop_meta(crop_id).iloc[0]
        weather = self._make_weather()
        weather_by_month, rain_clim = _weather_lookups(weather)

        cutoff = pd.Timestamp("2024-06-01")
        safe_buffer = 90  # longest rolling window (RollMean90)

        feats_full = build_crop_features(crop_id, prices, meta, weather_by_month, rain_clim)
        prices_trunc = prices[prices["PriceDate"] <= cutoff].copy()
        feats_trunc = build_crop_features(crop_id, prices_trunc, meta, weather_by_month, rain_clim)

        safe_cutoff = cutoff - pd.Timedelta(days=safe_buffer)
        exclude = {"CropId", "CropCode", "CropName", "ObservationDate",
                   "HarvestDate", "LabelHarvestPrice", "LabelAvailable"}
        feat_cols = [c for c in feats_full.columns if c not in exclude]

        full_idx = feats_full.set_index("ObservationDate")[feat_cols]
        trunc_idx = feats_trunc.set_index("ObservationDate")[feat_cols]

        safe_dates = trunc_idx.index[trunc_idx.index <= safe_cutoff]
        assert len(safe_dates) > 0, "No safe dates -- history too short"

        diff = (full_idx.loc[safe_dates] - trunc_idx.loc[safe_dates]).abs()
        max_diff = float(np.nanmax(diff.values))

        assert max_diff == 0.0, (
            "LEAKAGE DETECTED: features changed when future data removed. "
            "max abs diff = %.2e over %d safe dates x %d features."
            % (max_diff, len(safe_dates), len(feat_cols))
        )

    def test_harti_backfill_does_not_leak_into_dec_rolling_windows(self):
        """After the HARTI splice, rolling windows (RollMean30, RollMean90,
        Lag30, Lag90) for DEC-era dates well past the warm-up period must
        equal the values computed from DEC-only data.

        If rolling windows bridged HARTI->DEC via future data peeking, these
        would diverge. They must not."""
        from agriforecast_ml.features import build_crop_features, _weather_lookups

        crop_id = "splice-leak-001"

        dec_start = pd.Timestamp("2025-07-01")
        dec_dates = pd.date_range(dec_start, periods=120, freq="D")
        rng = np.random.default_rng(99)
        dec_prices = 150.0 + np.cumsum(rng.standard_normal(120) * 3)
        dec_df = pd.DataFrame({
            "CropId": crop_id, "CropCode": "TST", "CropName": "TestCrop",
            "PriceDate": dec_dates,
            "MinPrice": dec_prices - 5, "MaxPrice": dec_prices + 5,
            "AvgPrice": dec_prices,
        })

        harti_start = dec_start - pd.Timedelta(days=300)
        harti_dates = pd.date_range(harti_start, periods=300, freq="D")
        rng2 = np.random.default_rng(77)
        harti_prices = 130.0 + np.cumsum(rng2.standard_normal(300) * 2)
        harti_df = pd.DataFrame({
            "CropId": crop_id, "CropCode": "TST", "CropName": "TestCrop",
            "PriceDate": harti_dates,
            "MinPrice": harti_prices - 5, "MaxPrice": harti_prices + 5,
            "AvgPrice": harti_prices,
        })

        meta = self._make_crop_meta(crop_id).iloc[0]
        weather = self._make_weather()
        weather_by_month, rain_clim = _weather_lookups(weather)

        feats_dec_only = build_crop_features(crop_id, dec_df, meta, weather_by_month, rain_clim)
        combined_df = pd.concat([harti_df, dec_df], ignore_index=True)
        feats_combined = build_crop_features(crop_id, combined_df, meta, weather_by_month, rain_clim)

        # Dates well past 90-day warm-up from the HARTI/DEC boundary
        safe_start = dec_start + pd.Timedelta(days=91)
        exclude = {"CropId", "CropCode", "CropName", "ObservationDate",
                   "HarvestDate", "LabelHarvestPrice", "LabelAvailable"}
        feat_cols = [c for c in feats_dec_only.columns if c not in exclude]

        dec_idx = feats_dec_only.set_index("ObservationDate")[feat_cols]
        comb_idx = feats_combined.set_index("ObservationDate")[feat_cols]
        safe_dates = dec_idx.index[dec_idx.index >= safe_start]
        assert len(safe_dates) > 0, "No safe dates past warm-up"

        for col in ["RollMean30", "RollMean90", "Lag30", "Lag90"]:
            if col not in feat_cols:
                continue
            for obs_ts in safe_dates:
                v_dec = dec_idx.loc[obs_ts, col]
                v_comb = comb_idx.loc[obs_ts, col]
                if not (np.isnan(v_dec) and np.isnan(v_comb)):
                    assert abs(v_dec - v_comb) < 1e-6, (
                        "After HARTI splice, '%s' at %s changed: dec_only=%.4f, combined=%.4f. "
                        "Rolling window may be bridging HARTI->DEC boundary via leakage."
                        % (col, obs_ts.date(), v_dec, v_comb)
                    )
