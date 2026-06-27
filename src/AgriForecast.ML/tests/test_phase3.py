"""
AgriForecast ML – pytest suite (Phase 3).

Coverage:
  test_contract   – dataset.py feature_columns / contract_hash
  test_baselines  – carry_forward_pred + crop_mean_pred
  test_leakage    – leakage-by-truncation gold-standard
  test_predict    – predict_harvest: interval order, required keys,
                    cold-start, GUID case-normalization
  test_timeline   – bands widen with horizon, history leakage guard,
                    months cap, no-history crop graceful degradation
  test_gate       – promoted model metadata honesty
                    (beats_baseline=False -> active_predictor=crop_mean_fallback)
"""
from __future__ import annotations

import json
import sys
import os
from datetime import date
from pathlib import Path

import numpy as np
import pandas as pd
import pytest

# ---------------------------------------------------------------------------
# Path setup: ensure we can import the package from the ML root
# ---------------------------------------------------------------------------
ML_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ML_ROOT))

# Crop IDs confirmed in the live DB (Brinjal – has GrowthPeriodDays=90 and
# training data, so the fallback has per-crop quantiles for it).
BRINJAL_ID   = "b44bacdb-042f-4286-ac23-02bc4cac4486"
RIDGE_ID     = "e384f58a-addf-40a4-aff0-10bebe8bc08f"   # GrowthPeriodDays=60
UNKNOWN_GUID = "00000000-0000-0000-0000-000000000000"

PLANT_DATE = date(2026, 1, 15)
AS_OF      = date(2026, 1, 15)


# ===========================================================================
# 1. CONTRACT  (dataset.py)
# ===========================================================================

class TestContract:
    """dataset.feature_columns and contract_hash behave correctly."""

    def _make_df(self, extra_cols=None):
        """Return a minimal DataFrame that mirrors the real CropFeatureDaily schema."""
        cols = {
            # Excluded – must NOT appear in feature_columns
            "CropCode": ["BRJ"],
            "CropName": ["Brinjal"],
            "ObservationDate": [date(2025, 1, 1)],
            "ComputedAtUtc": [None],
            "HarvestDate": [None],
            "LabelHarvestPrice": [100.0],
            "LabelAvailable": [1],
            "Year": [2025],
            # Kept numeric features
            "AvgPrice": [120.0],
            "RollMean30": [115.0],
            "Lag7": [118.0],
            "MonthNum": [1],
            "SeasonMaha": [1],
            "GrowthPeriodDays": [90],
            # Categorical identity – MUST appear at end
            "CropId": [BRINJAL_ID],
        }
        if extra_cols:
            cols.update(extra_cols)
        return pd.DataFrame(cols)

    def test_excludes_label_and_key_cols(self):
        from agriforecast_ml.train.dataset import feature_columns, _EXCLUDE

        df = self._make_df()
        cols = feature_columns(df)

        excluded = set(_EXCLUDE) | {"CropCode", "CropName"}  # keys too
        for bad in excluded:
            assert bad not in cols, (
                f"feature_columns must not include '{bad}' — it is label/key/bookkeeping"
            )

    def test_cropid_is_included(self):
        from agriforecast_ml.train.dataset import feature_columns

        df = self._make_df()
        cols = feature_columns(df)
        assert "CropId" in cols, "CropId (crop identity) must be in feature_columns"

    def test_cropid_is_last(self):
        """Categorical columns must come at the tail of the ordered list."""
        from agriforecast_ml.train.dataset import feature_columns, CATEGORICAL_COLS

        df = self._make_df()
        cols = feature_columns(df)
        for cat in CATEGORICAL_COLS:
            if cat in cols:
                # last len(CATEGORICAL_COLS) positions
                assert cat in cols[len(cols) - len(CATEGORICAL_COLS):], (
                    f"Categorical col '{cat}' must be at the end of feature_columns"
                )

    def test_contract_hash_stable(self):
        """Same column list -> same hash, always."""
        from agriforecast_ml.train.dataset import feature_columns, contract_hash

        df = self._make_df()
        cols = feature_columns(df)
        h1 = contract_hash(cols)
        h2 = contract_hash(cols)
        assert h1 == h2, "contract_hash must be deterministic"

    def test_contract_hash_changes_on_column_change(self):
        """Adding a column must change the hash (detects schema drift)."""
        from agriforecast_ml.train.dataset import feature_columns, contract_hash

        df1 = self._make_df()
        df2 = self._make_df(extra_cols={"NewFeature": [99.9]})

        cols1 = feature_columns(df1)
        cols2 = feature_columns(df2)

        assert cols1 != cols2, "Test setup error: column lists should differ"
        assert contract_hash(cols1) != contract_hash(cols2), (
            "contract_hash must change when column list changes"
        )

    def test_numeric_cols_sorted_before_categorical(self):
        """Numeric features must be alphabetically sorted; CropId at the end."""
        from agriforecast_ml.train.dataset import feature_columns, CATEGORICAL_COLS

        df = self._make_df()
        cols = feature_columns(df)
        non_cat = [c for c in cols if c not in CATEGORICAL_COLS]
        assert non_cat == sorted(non_cat), (
            "Numeric features in feature_columns must be sorted alphabetically"
        )


# ===========================================================================
# 2. BASELINES  (baselines.py)
# ===========================================================================

class TestBaselines:

    def _train_df(self):
        return pd.DataFrame({
            "CropId": ["A", "A", "A", "B", "B"],
            "LabelHarvestPrice": [100.0, 110.0, 90.0, 200.0, 210.0],
        })

    def _eval_df(self):
        return pd.DataFrame({
            "CropId": ["A", "B", "C"],   # C is unseen in training
            "AvgPrice": [105.0, 195.0, 150.0],
        })

    def test_carry_forward_returns_avgprice(self):
        from agriforecast_ml.train.baselines import carry_forward_pred

        eval_df = self._eval_df()
        pred = carry_forward_pred(eval_df)

        assert pred.shape[0] == len(eval_df), "carry_forward_pred must return one value per row"
        np.testing.assert_array_almost_equal(
            pred, eval_df["AvgPrice"].to_numpy(dtype=float),
            err_msg="carry_forward_pred must return AvgPrice values unchanged"
        )

    def test_carry_forward_dtype_float(self):
        from agriforecast_ml.train.baselines import carry_forward_pred

        pred = carry_forward_pred(self._eval_df())
        assert pred.dtype.kind == "f", "carry_forward_pred output must be float"

    def test_crop_mean_known_crop(self):
        from agriforecast_ml.train.baselines import crop_mean_pred

        train = self._train_df()
        eval_df = self._eval_df()
        pred = crop_mean_pred(train, eval_df)

        # Crop A mean = (100+110+90)/3 = 100
        assert abs(pred[0] - 100.0) < 1e-9, (
            f"Crop A mean should be 100.0, got {pred[0]}"
        )
        # Crop B mean = (200+210)/2 = 205
        assert abs(pred[1] - 205.0) < 1e-9, (
            f"Crop B mean should be 205.0, got {pred[1]}"
        )

    def test_crop_mean_unseen_crop_uses_global(self):
        """Unseen crop (C) must fall back to global mean, not NaN or error."""
        from agriforecast_ml.train.baselines import crop_mean_pred

        train = self._train_df()
        eval_df = self._eval_df()
        pred = crop_mean_pred(train, eval_df)

        global_mean = train["LabelHarvestPrice"].mean()   # (100+110+90+200+210)/5 = 142
        assert not np.isnan(pred[2]), (
            "Unseen crop must fall back to global mean, not produce NaN"
        )
        assert abs(pred[2] - global_mean) < 1e-9, (
            f"Unseen crop fallback should be global mean {global_mean:.1f}, got {pred[2]}"
        )

    def test_crop_mean_one_value_per_row(self):
        from agriforecast_ml.train.baselines import crop_mean_pred

        pred = crop_mean_pred(self._train_df(), self._eval_df())
        assert pred.shape[0] == len(self._eval_df())


# ===========================================================================
# 3. LEAKAGE-BY-TRUNCATION  (features.py) – the gold-standard check
# ===========================================================================

class TestLeakageByTruncation:
    """
    Rebuild features for one crop twice: once with the full price history, once
    with all data after a cutoff date removed.  Features for dates well before
    the cutoff must be **bit-identical** across both builds.  Any non-zero
    difference proves a feature peeked at future data.

    We compare dates at least 90 days (the longest rolling window) before the
    cutoff to ensure no window can reach across it.

    This is the decisive leakage test.  max diff must be 0.00e+00.
    """

    CUTOFF = pd.Timestamp("2026-01-15")
    SAFE_BUFFER_DAYS = 90   # must exceed the longest rolling window (RollMean90)

    def _load_brinjal(self):
        from agriforecast_ml import load
        prices = load.load_prices()
        brinjal = prices[prices["CropName"] == "Brinjal"].copy()
        assert len(brinjal) > 0, "Brinjal price data not found — check DB connection"
        return brinjal

    def test_features_identical_with_and_without_future_data(self):
        from agriforecast_ml import load, features

        brinjal = self._load_brinjal()
        crops   = load.load_crops()
        weather = load.load_weather()

        # Build full feature set
        full = features.build_all(brinjal, crops, weather)

        # Build truncated feature set (future data removed)
        brinjal_trunc = brinjal[brinjal["PriceDate"] <= self.CUTOFF].copy()
        trunc = features.build_all(brinjal_trunc, crops, weather)

        # Columns to compare (exclude the label – which legitimately uses future data)
        exclude = {
            "CropId", "CropCode", "CropName", "ObservationDate",
            "HarvestDate", "LabelHarvestPrice", "LabelAvailable",
        }
        feat_cols = [c for c in full.columns if c not in exclude]

        full_indexed  = full.set_index("ObservationDate")[feat_cols]
        trunc_indexed = trunc.set_index("ObservationDate")[feat_cols]

        # Safe dates: at least SAFE_BUFFER_DAYS before cutoff, present in both
        safe_dates = trunc_indexed.index[
            trunc_indexed.index <= self.CUTOFF - pd.Timedelta(days=self.SAFE_BUFFER_DAYS)
        ]
        assert len(safe_dates) > 0, (
            "No safe dates found — history might be too short for this cutoff"
        )

        diff = (full_indexed.loc[safe_dates] - trunc_indexed.loc[safe_dates]).abs()
        max_diff = float(np.nanmax(diff.values))

        assert max_diff == 0.0, (
            f"LEAKAGE DETECTED: features changed when future data was removed. "
            f"max abs diff = {max_diff:.2e} over {len(safe_dates)} dates x "
            f"{len(feat_cols)} features.  A feature is peeking at future data."
        )


# ===========================================================================
# 3b. FX POINT-IN-TIME AS-OF JOIN  (features._attach_fx)
# ===========================================================================

class TestFxAsOfJoin:
    """
    FxUsdLkr is a national economic indicator merged via a point-in-time
    (as-of, backward) join: for an observation on date D, only an FX rate with
    date <= D may be used.  No FX value may come from a date AFTER D.

    Data today is sparse (FX is latest-only — open.er-api.com gives no history),
    so the real column is all-NULL; we therefore inject a synthetic FX series to
    exercise the as-of logic deterministically.
    """

    def test_fx_never_from_future_date(self):
        from agriforecast_ml import load, features

        prices  = load.load_prices()
        brinjal = prices[prices["CropName"] == "Brinjal"].copy()
        assert len(brinjal) > 0, "Brinjal price data not found — check DB connection"
        crops   = load.load_crops()
        weather = load.load_weather()

        # Synthetic FX series spanning the price window, with distinct values so
        # we can trace exactly which row each observation picked up.
        lo, hi = brinjal["PriceDate"].min(), brinjal["PriceDate"].max()
        fx_dates = pd.date_range(lo, hi, freq="37D")  # irregular cadence
        fx = pd.DataFrame({
            "date": fx_dates,
            "fx_usd_lkr": np.arange(len(fx_dates), dtype=float) + 300.0,
        })

        feats = features.build_all(brinjal, crops, weather, fx)
        assert "FxUsdLkr" in feats.columns, "FxUsdLkr column missing after build"

        # For each row, the FX value must equal the most recent fx <= ObservationDate.
        fx_sorted = fx.sort_values("date").reset_index(drop=True)
        joined = feats.dropna(subset=["FxUsdLkr"])
        assert len(joined) > 0, "Synthetic FX should populate at least some rows"
        for obs, fxv in zip(joined["ObservationDate"], joined["FxUsdLkr"]):
            eligible = fx_sorted[fx_sorted["date"] <= obs]
            assert len(eligible) > 0, (
                f"FX value {fxv} attached to {obs.date()} but no FX date <= it exists "
                f"— this would be a future (leaking) FX rate"
            )
            expected = float(eligible.iloc[-1]["fx_usd_lkr"])
            assert fxv == expected, (
                f"as-of mismatch on {obs.date()}: got {fxv}, expected {expected} "
                f"(most recent FX <= observation date)"
            )

    def test_fx_all_null_when_fx_after_window(self):
        """If the only FX row post-dates every observation, FxUsdLkr must be all-NULL
        (the current forward-investment reality), not silently filled."""
        from agriforecast_ml import load, features

        prices  = load.load_prices()
        brinjal = prices[prices["CropName"] == "Brinjal"].copy()
        crops   = load.load_crops()
        weather = load.load_weather()

        future = brinjal["PriceDate"].max() + pd.Timedelta(days=1)
        fx = pd.DataFrame({"date": [future], "fx_usd_lkr": [336.67]})

        feats = features.build_all(brinjal, crops, weather, fx)
        assert feats["FxUsdLkr"].isna().all(), (
            "FxUsdLkr must be all-NULL when the only FX rate post-dates the window "
            "(no backward-looking rate exists) — never a future leak"
        )


# ===========================================================================
# 4. PREDICT_HARVEST  (serving/predict.py)
# ===========================================================================

class TestPredictHarvest:
    """
    Tests run against the live DB + promoted model (v3, crop_mean_fallback).
    These exercise the actual HTTP-equivalent path through predict_harvest().
    """

    def _call(self, crop_id, plant_date=PLANT_DATE):
        from agriforecast_ml.serving.predict import predict_harvest
        return predict_harvest(crop_id, plant_date)

    def test_required_keys_present(self):
        result = self._call(BRINJAL_ID)
        required = {
            "cropId", "cropName", "plantDate", "harvestDate",
            "growthPeriodDays", "predictedPrice",
            "lowerBound", "upperBound", "confidence",
            "activePredictor", "modelVersion", "explanation", "topFactors",
        }
        missing = required - set(result.keys())
        assert not missing, f"predict_harvest response missing keys: {missing}"

    def test_interval_ordered(self):
        """lowerBound <= predictedPrice <= upperBound."""
        r = self._call(BRINJAL_ID)
        assert r["lowerBound"] <= r["predictedPrice"], (
            f"lowerBound ({r['lowerBound']}) must be <= predictedPrice ({r['predictedPrice']})"
        )
        assert r["predictedPrice"] <= r["upperBound"], (
            f"predictedPrice ({r['predictedPrice']}) must be <= upperBound ({r['upperBound']})"
        )

    def test_crop_id_echoed_lowercase(self):
        """Response cropId must be lowercase (normalization contract)."""
        r = self._call(BRINJAL_ID.upper())
        assert r["cropId"] == BRINJAL_ID.lower(), (
            f"cropId in response should be lowercase; got {r['cropId']!r}"
        )

    def test_guid_case_normalization(self):
        """UPPERCASE crop_id == lowercase crop_id == same result (regression guard)."""
        r_lower = self._call(BRINJAL_ID.lower())
        r_upper = self._call(BRINJAL_ID.upper())
        assert r_lower["predictedPrice"] == r_upper["predictedPrice"], (
            "GUID case-normalization regression: uppercase and lowercase GUID "
            "must produce the same predictedPrice"
        )
        assert r_lower["lowerBound"] == r_upper["lowerBound"]
        assert r_lower["upperBound"] == r_upper["upperBound"]

    def test_cold_start_unknown_crop_does_not_raise(self):
        """Unknown crop GUID must not raise — must return Low confidence."""
        try:
            r = self._call(UNKNOWN_GUID)
        except Exception as exc:
            pytest.fail(
                f"predict_harvest raised for unknown crop: {type(exc).__name__}: {exc}"
            )
        assert r["confidence"] == "Low", (
            f"Unknown/cold-start crop must return confidence='Low', got {r['confidence']!r}"
        )

    def test_cold_start_returns_valid_interval(self):
        """Even for an unknown crop, the price interval must be valid (ordered, > 0)."""
        r = self._call(UNKNOWN_GUID)
        assert r["lowerBound"] <= r["predictedPrice"] <= r["upperBound"], (
            "Cold-start interval must be ordered: lower <= predicted <= upper"
        )
        assert r["predictedPrice"] > 0, "Cold-start predictedPrice must be positive"

    def test_top_factors_is_list(self):
        """topFactors must always be a list (may be empty for fallback predictor)."""
        r = self._call(BRINJAL_ID)
        assert isinstance(r["topFactors"], list), (
            f"topFactors must be a list, got {type(r['topFactors'])}"
        )

    def test_top_factors_empty_unless_model_active(self):
        """Honesty contract: SHAP factors are only surfaced when the ML MODEL is the
        active predictor. While the fallback serves, topFactors must be empty —
        there is nothing to 'explain' about a historical average."""
        r = self._call(BRINJAL_ID)
        if r["activePredictor"] != "model":
            assert r["topFactors"] == [], (
                f"topFactors must be [] under '{r['activePredictor']}', got {r['topFactors']}"
            )

    def test_active_predictor_matches_promoted_model(self):
        """activePredictor must match what the registry says (no silent override)."""
        from agriforecast_ml.registry import registry
        _, meta = registry.load_promoted()
        r = self._call(BRINJAL_ID)
        assert r["activePredictor"] == meta["active_predictor"], (
            f"Serving activePredictor {r['activePredictor']!r} does not match "
            f"registry active_predictor {meta['active_predictor']!r}"
        )


# ===========================================================================
# 5. TIMELINE  (serving/predict.py)
# ===========================================================================

class TestTimeline:

    def _call(self, crop_id=BRINJAL_ID, as_of=AS_OF, months=12):
        from agriforecast_ml.serving.predict import timeline
        return timeline(crop_id, as_of, months)

    def test_required_top_level_keys(self):
        r = self._call()
        required = {
            "cropId", "cropName", "asOf", "activePredictor",
            "confidence", "modelVersion", "explanation", "history", "forecast",
        }
        missing = required - set(r.keys())
        assert not missing, f"timeline response missing keys: {missing}"

    def test_forecast_bands_widen_with_horizon(self):
        """Each successive horizon must have a wider or equal band (lower <= previous lower,
        upper >= previous upper), because uncertainty grows with horizon."""
        r = self._call(months=12)
        forecast = r["forecast"]
        assert len(forecast) >= 2, "Need at least two horizon points to compare band widths"

        for i in range(1, len(forecast)):
            prev_spread = forecast[i - 1]["upperBound"] - forecast[i - 1]["lowerBound"]
            curr_spread = forecast[i]["upperBound"]     - forecast[i]["lowerBound"]
            assert curr_spread >= prev_spread - 1e-9, (
                f"Band must widen (or stay equal) with horizon: "
                f"h={forecast[i-1]['horizonMonths']} spread={prev_spread:.2f}, "
                f"h={forecast[i]['horizonMonths']} spread={curr_spread:.2f} — NARROWED"
            )

    def test_history_no_future_leakage(self):
        """Every history entry must have a date <= as_of (no future peek)."""
        r = self._call(as_of=AS_OF)
        as_of_str = AS_OF.isoformat()[:7]  # "YYYY-MM"
        for entry in r["history"]:
            assert entry["month"] <= as_of_str, (
                f"History entry {entry['month']} is AFTER as_of={as_of_str} — leakage!"
            )

    def test_months_param_caps_horizons(self):
        """Setting months=3 must exclude horizons > 3."""
        r = self._call(months=3)
        for entry in r["forecast"]:
            assert entry["horizonMonths"] <= 3, (
                f"horizonMonths={entry['horizonMonths']} exceeds months=3 cap"
            )

    def test_months_param_no_cap(self):
        """months=12 should produce all four standard horizons [1,3,6,12]."""
        r = self._call(months=12)
        horizons = [e["horizonMonths"] for e in r["forecast"]]
        assert horizons == [1, 3, 6, 12], (
            f"Expected horizons [1,3,6,12] with months=12, got {horizons}"
        )

    def test_unknown_crop_no_raise(self):
        """Unknown crop must not raise — degrade gracefully."""
        try:
            r = self._call(crop_id=UNKNOWN_GUID)
        except Exception as exc:
            pytest.fail(
                f"timeline raised for unknown crop: {type(exc).__name__}: {exc}"
            )

    def test_unknown_crop_empty_or_small_history(self):
        """Unknown crop's history must be empty (no price data)."""
        r = self._call(crop_id=UNKNOWN_GUID)
        assert isinstance(r["history"], list), "history must be a list"
        assert len(r["history"]) == 0, (
            f"Expected empty history for unknown crop, got {len(r['history'])} entries"
        )

    def test_unknown_crop_confidence_low(self):
        """Unknown crop must return Low confidence — no per-crop fallback available."""
        r = self._call(crop_id=UNKNOWN_GUID)
        assert r["confidence"] == "Low", (
            f"Unknown crop must return confidence='Low', got {r['confidence']!r}"
        )

    def test_forecast_prices_are_positive(self):
        """All predictedPrice values in forecast must be > 0."""
        r = self._call()
        for entry in r["forecast"]:
            assert entry["predictedPrice"] > 0, (
                f"predictedPrice at horizon {entry['horizonMonths']} is not positive: "
                f"{entry['predictedPrice']}"
            )

    def test_forecast_intervals_ordered(self):
        """Each forecast entry: lowerBound <= predictedPrice <= upperBound."""
        r = self._call()
        for entry in r["forecast"]:
            h = entry["horizonMonths"]
            assert entry["lowerBound"] <= entry["predictedPrice"], (
                f"h={h}: lowerBound ({entry['lowerBound']}) > predictedPrice ({entry['predictedPrice']})"
            )
            assert entry["predictedPrice"] <= entry["upperBound"], (
                f"h={h}: predictedPrice ({entry['predictedPrice']}) > upperBound ({entry['upperBound']})"
            )


# ===========================================================================
# 6. GATE HONESTY  (model.py / registry metadata)
# ===========================================================================

class TestGateHonesty:
    """
    The promoted model (v3) lost to the crop-mean baseline.  The honesty rule:
      beats_baseline must be False
      active_predictor must be "crop_mean_fallback"
    If either is wrong the gate is broken and we may be serving an inferior
    ML model while thinking we serve the safer fallback.
    """

    def _promoted_meta(self):
        from agriforecast_ml.registry import registry
        _, meta = registry.load_promoted()
        assert meta is not None, "No promoted model found in registry"
        return meta

    def test_beats_baseline_is_false(self):
        """Model A lost to crop_mean; beats_baseline must be False."""
        meta = self._promoted_meta()
        assert meta["beats_baseline"] is False, (
            f"beats_baseline should be False (model lost to crop_mean), "
            f"but got {meta['beats_baseline']!r}.  The promotion gate is lying."
        )

    def test_active_predictor_is_crop_mean_fallback(self):
        """When beats_baseline is False, active_predictor must be crop_mean_fallback."""
        meta = self._promoted_meta()
        assert meta["active_predictor"] == "crop_mean_fallback", (
            f"active_predictor should be 'crop_mean_fallback' when model loses, "
            f"got {meta['active_predictor']!r}"
        )

    def test_beats_baseline_consistent_with_cv_numbers(self):
        """The gate decision must be arithmetically consistent with stored CV MAEs."""
        meta = self._promoted_meta()
        cv = meta["cv"]
        model_mae   = cv["model_MAE"]
        carry_mae   = cv["carry_MAE"]
        cropmean_mae = cv["cropmean_MAE"]
        best_baseline_mae = min(carry_mae, cropmean_mae)

        # beats_baseline = (model_mae < best_baseline_mae)
        expected_beats = model_mae < best_baseline_mae
        assert meta["beats_baseline"] == expected_beats, (
            f"beats_baseline={meta['beats_baseline']} is inconsistent with CV numbers: "
            f"model_MAE={model_mae} vs best_baseline_MAE={best_baseline_mae:.2f}. "
            f"Expected beats_baseline={expected_beats}."
        )

    def test_best_baseline_recorded_correctly(self):
        """The recorded best_baseline must actually BE the minimum of carry/cropmean."""
        meta = self._promoted_meta()
        cv = meta["cv"]
        carry_mae    = cv["carry_MAE"]
        cropmean_mae = cv["cropmean_MAE"]
        reported     = cv["best_baseline"]

        if cropmean_mae <= carry_mae:
            expected = "crop_mean"
        else:
            expected = "carry_forward"

        assert reported == expected, (
            f"best_baseline recorded as {reported!r} but arithmetic says {expected!r}. "
            f"carry_MAE={carry_mae}, cropmean_MAE={cropmean_mae}"
        )

    def test_active_predictor_consistent_with_beats_baseline(self):
        """active_predictor must equal 'model' iff beats_baseline is True."""
        meta = self._promoted_meta()
        if meta["beats_baseline"]:
            assert meta["active_predictor"] == "model", (
                "beats_baseline=True but active_predictor is not 'model'"
            )
        else:
            assert meta["active_predictor"] == "crop_mean_fallback", (
                "beats_baseline=False but active_predictor is not 'crop_mean_fallback'"
            )

    def test_model_mae_greater_than_best_baseline(self):
        """Concrete check: model_MAE > best_baseline_MAE (the reason it was not promoted)."""
        meta = self._promoted_meta()
        cv = meta["cv"]
        model_mae        = cv["model_MAE"]
        best_baseline_mae = min(cv["carry_MAE"], cv["cropmean_MAE"])
        assert model_mae > best_baseline_mae, (
            f"Model MAE {model_mae} should be > best baseline MAE {best_baseline_mae:.2f} "
            f"for current data volume — this is a known limitation, not a bug."
        )
