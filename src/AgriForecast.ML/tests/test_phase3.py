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
        """Honesty contract: factor codes are surfaced when an ML model is active
        (active_predictor in _SERVED_ML_KINDS) and must be OMITTED under the
        crop_mean_fallback (there is nothing to explain about a historical
        average; the FE shows a "no breakdown" note). API-5 changed the fallback
        behavior from `topFactors: []` to omitting the key entirely.
        Bidirectional: factors present + non-empty for a known crop with ML
        serving; key absent when fallback serves."""
        r = self._call(BRINJAL_ID)
        active = r["activePredictor"]
        if active in _SERVED_ML_KINDS:
            # ML model is serving: factor codes must be surfaced for a known crop.
            assert r["topFactors"] != [], (
                f"topFactors must be non-empty when ML kind {active!r} is active "
                f"and the crop is known (BRINJAL_ID), got []"
            )
        else:
            # Fallback (crop_mean_fallback): the key is omitted entirely.
            assert "topFactors" not in r, (
                f"topFactors must be OMITTED under fallback predictor {active!r}, "
                f"got {r.get('topFactors')}"
            )

    def test_active_predictor_matches_promoted_model(self):
        """activePredictor must be coherent with what the registry says (no silent
        override).

        v13 REPIN: the response `activePredictor` is now PER-CROP. For a model-
        served (history-gated) crop it is the ARTIFACT kind the crop went through
        (served_ml_kind, e.g. 'model'); for a non-served crop it is
        'crop_mean_fallback'. The metadata's top-level `active_predictor` describes
        the WHOLE shipped predictor ('hybrid' when a gate is present). So the old
        exact-equality no longer holds across the gate; assert per-crop coherence
        instead. BRINJAL_ID is inside the v13 served_on_crops gate."""
        from agriforecast_ml.registry import registry
        from agriforecast_ml.serving import predict as P
        _, meta = registry.load_promoted()
        r = self._call(BRINJAL_ID)
        active = r["activePredictor"]
        served = meta.get("active_predictor") in ("hybrid",) or "served_on_crops" in meta
        if served and P._is_model_served(BRINJAL_ID) and meta.get("beats_baseline"):
            # Gated + model wins -> response reports the artifact kind it served.
            assert active == meta["served_ml_kind"], (
                f"Serving activePredictor {active!r} for a gated crop must be the "
                f"served_ml_kind {meta['served_ml_kind']!r}."
            )
        elif not meta.get("beats_baseline"):
            # No ML promoted -> both must read crop_mean_fallback.
            assert active == "crop_mean_fallback" == meta["active_predictor"]
        else:
            # Non-gated crop under a winning model -> fallback route.
            assert active == "crop_mean_fallback"


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

    def _assert_bands_widen(self, forecast):
        assert len(forecast) >= 2, "Need at least two horizon points to compare band widths"
        for i in range(1, len(forecast)):
            prev_spread = forecast[i - 1]["upperBound"] - forecast[i - 1]["lowerBound"]
            curr_spread = forecast[i]["upperBound"]     - forecast[i]["lowerBound"]
            assert curr_spread >= prev_spread - 1e-9, (
                f"Band must widen (or stay equal) with horizon: "
                f"h={forecast[i-1]['horizonMonths']} spread={prev_spread:.2f}, "
                f"h={forecast[i]['horizonMonths']} spread={curr_spread:.2f} — NARROWED"
            )

    def test_forecast_bands_widen_with_horizon(self):
        """Each successive horizon must have a wider or equal band (lower <= previous lower,
        upper >= previous upper), because uncertainty grows with horizon."""
        r = self._call(months=12)
        self._assert_bands_widen(r["forecast"])

    def test_forecast_bands_widen_degenerate_cold_start(self, monkeypatch):
        """Cold-start degenerate case: a crop whose fallback quantiles collapse to a
        single point (p10==p50==p90, e.g. a one-row crop). The band must NOT widen
        (it stays a zero-width point at every horizon: sqrt-scaling of a zero gap is
        still zero) — monotonicity must not blow up on the degenerate spread. Guards
        the interval-widening code against divide-by / assumption-of-nonzero-gap."""
        from agriforecast_ml.serving import predict as P
        # A crop whose quantiles collapse to a single point (p10==p50==p90).
        # min_history_obs=0 so the point-quantile per-crop entry is actually used
        # (we are testing the widening math on a zero-width band, not the ladder).
        degen_id = "11111111-1111-1111-1111-111111111111"
        payload = dict(P._PAYLOAD or {})
        fb = dict(payload.get("fallback") or {})
        per = dict(fb.get("per_crop") or {})
        per[degen_id] = {"p10": 200.0, "p50": 200.0, "p90": 200.0, "n_obs": 1}
        fb = {**fb, "per_crop": per, "min_history_obs": 0}
        payload = {**payload, "fallback": fb, "beats_baseline": False}  # force fallback path
        monkeypatch.setattr(P, "_PAYLOAD", payload)
        r = P.timeline(degen_id, AS_OF, months=12)
        self._assert_bands_widen(r["forecast"])
        for e in r["forecast"]:
            assert e["lowerBound"] == e["predictedPrice"] == e["upperBound"] == 200.0, (
                f"Degenerate single-row crop must stay a zero-width point at every "
                f"horizon; h={e['horizonMonths']} got {e}"
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

# ---------------------------------------------------------------------------
# Supported served ML kinds.  When beats_baseline=True the serving path must
# use one of these -- never an unrecognised string that bypasses the guard.
# ---------------------------------------------------------------------------
_SERVED_ML_KINDS = frozenset({"model", "residual"})


class TestGateHonesty:
    """
    Honesty invariants for the promoted model's metadata.

    Post-v8 reality: the promoted model is a residual ML model that BEATS its
    baselines on honest walk-forward folds (no leakage):
      beats_baseline=True, active_predictor='residual',
      best_ml_MAE 100.51 < best_baseline_MAE 114.93 (recency_weighted_mean).

    These tests are invariant-based, not value-pinning tests.  They will catch:
      - A gate that marks beats_baseline=True when the arithmetic says False.
      - A gate that serves the wrong predictor for the beats_baseline outcome.
      - A metadata record that misreports which baseline or ML variant won.
    They will NOT break simply because a new model has a different MAE --
    only if the metadata is internally inconsistent or the gate logic is wrong.
    """

    def _promoted_meta(self):
        from agriforecast_ml.registry import registry
        _, meta = registry.load_promoted()
        assert meta is not None, "No promoted model found in registry"
        return meta

    def _best_ml_mae(self, cv: dict) -> float:
        """Return the best (lowest) ML MAE from the cv block.

        Prefers the explicit 'best_ml_MAE' field (stored since v8) and falls
        back to 'model_MAE' for older metadata shapes (v3-style).
        """
        return cv.get("best_ml_MAE", cv["model_MAE"])

    def _best_baseline_mae(self, cv: dict) -> float:
        """Return the best (lowest) baseline MAE from the cv block.

        Prefers the explicit 'best_baseline_MAE' field (stored since v8, covers
        carry / crop_mean / recency_weighted_mean) and falls back to
        min(carry_MAE, cropmean_MAE) for older metadata shapes (v3-style).
        """
        if "best_baseline_MAE" in cv:
            return cv["best_baseline_MAE"]
        return min(cv["carry_MAE"], cv["cropmean_MAE"])

    def test_beats_baseline_consistent_with_cv_numbers(self):
        """beats_baseline must equal (best_ml_MAE < best_baseline_MAE) -- no lying."""
        meta = self._promoted_meta()
        cv = meta["cv"]
        best_ml_mae       = self._best_ml_mae(cv)
        best_baseline_mae = self._best_baseline_mae(cv)

        expected_beats = best_ml_mae < best_baseline_mae
        assert meta["beats_baseline"] == expected_beats, (
            f"beats_baseline={meta['beats_baseline']} is inconsistent with CV numbers: "
            f"best_ml_MAE={best_ml_mae:.2f} vs best_baseline_MAE={best_baseline_mae:.2f}. "
            f"Expected beats_baseline={expected_beats}. "
            f"The promotion gate is recording a false result."
        )

    def test_best_baseline_recorded_correctly(self):
        """The recorded best_baseline must match the minimum over all stored baselines."""
        meta = self._promoted_meta()
        cv = meta["cv"]
        reported = cv["best_baseline"]

        # Build a mapping of every baseline present in the cv block.
        candidates = {}
        if "carry_MAE" in cv:
            candidates["carry_forward"] = cv["carry_MAE"]
        if "cropmean_MAE" in cv:
            candidates["crop_mean"] = cv["cropmean_MAE"]
        if "recencymean_MAE" in cv:
            candidates["recency_weighted_mean"] = cv["recencymean_MAE"]

        assert candidates, "cv block has no recognisable baseline MAE fields"

        best_mae = min(candidates.values())

        # Allow a tie: any baseline whose MAE equals the minimum is a valid winner.
        tied_names = {k for k, v in candidates.items() if v == best_mae}
        assert reported in tied_names, (
            f"best_baseline recorded as {reported!r} but the minimum baseline "
            f"({best_mae:.2f}) belongs to {tied_names}. "
            f"Full baselines: {candidates}"
        )

    def test_active_predictor_consistent_with_beats_baseline(self):
        """active_predictor must be a served ML kind iff beats_baseline is True."""
        meta = self._promoted_meta()
        beats = meta["beats_baseline"]
        active = meta["active_predictor"]

        if beats:
            # v13 REPIN: the shipped predictor is the history-gated HYBRID (pooled
            # model on gated crops + recency-mean baseline on thin crops), so
            # active_predictor is now "hybrid" when the payload carries a
            # served_on_crops gate. The ARTIFACT kind the gated crops go through is
            # still served_ml_kind ("model"/"residual"); active_predictor describes
            # the WHOLE served predictor, not the artifact kind, so the pre-v13
            # active==served_ml_kind identity no longer holds by construction.
            if "served_on_crops" in meta:
                assert active == "hybrid", (
                    f"beats_baseline=True with a served_on_crops gate but "
                    f"active_predictor={active!r} (expected 'hybrid')."
                )
                assert meta.get("served_ml_kind") in _SERVED_ML_KINDS, (
                    f"hybrid gated crops must go through a recognised served ML "
                    f"kind; served_ml_kind={meta.get('served_ml_kind')!r}."
                )
            else:
                # Pre-v13 payloads (no gate): active_predictor == served_ml_kind.
                expected_kind = meta.get("served_ml_kind", None)
                if expected_kind is not None:
                    assert active == expected_kind, (
                        f"beats_baseline=True but active_predictor={active!r} does not "
                        f"match served_ml_kind={expected_kind!r} recorded in metadata."
                    )
                else:
                    assert active in _SERVED_ML_KINDS, (
                        f"beats_baseline=True but active_predictor={active!r} is not "
                        f"a recognised served ML kind {_SERVED_ML_KINDS}."
                    )
        else:
            assert active == "crop_mean_fallback", (
                f"beats_baseline=False but active_predictor={active!r} -- "
                f"must be 'crop_mean_fallback' when the model loses."
            )

    def test_best_ml_beats_best_baseline_when_promoted(self):
        """When beats_baseline=True: best_ml_MAE < best_baseline_MAE (honest win).
        When beats_baseline=False: best_ml_MAE >= best_baseline_MAE (fair loss).
        """
        meta = self._promoted_meta()
        cv = meta["cv"]
        best_ml_mae       = self._best_ml_mae(cv)
        best_baseline_mae = self._best_baseline_mae(cv)

        if meta["beats_baseline"]:
            assert best_ml_mae < best_baseline_mae, (
                f"beats_baseline=True but best_ml_MAE {best_ml_mae:.2f} >= "
                f"best_baseline_MAE {best_baseline_mae:.2f}. "
                f"The gate is claiming a win it did not earn."
            )
        else:
            assert best_ml_mae >= best_baseline_mae, (
                f"beats_baseline=False but best_ml_MAE {best_ml_mae:.2f} < "
                f"best_baseline_MAE {best_baseline_mae:.2f}. "
                f"The gate is hiding a win -- the ML model should be serving."
            )

    def test_best_ml_kind_matches_served_ml_kind(self):
        """The winning ML variant in cv ('best_ml') must match served_ml_kind.

        v13 REPIN: the winning candidate is now the 'hybrid' (history-gated pooled
        model + recency-mean). The hybrid is NOT itself a servable artifact kind —
        its gated crops go through the pooled 'model' artifact and its thin crops
        take the baseline (routed via served_on_crops). So for a hybrid winner the
        identity is best_ml=='hybrid' AND served_ml_kind in {model,residual}; for
        pre-v13 winners the old identity best_ml==served_ml_kind still holds.
        """
        meta = self._promoted_meta()
        cv = meta["cv"]
        if "best_ml" not in cv or "served_ml_kind" not in meta:
            pytest.skip("Metadata predates best_ml/served_ml_kind fields (v3-style)")
        if cv["best_ml"] == "hybrid":
            assert "served_on_crops" in meta, (
                "best_ml='hybrid' but no served_on_crops gate recorded."
            )
            assert meta["served_ml_kind"] in _SERVED_ML_KINDS, (
                f"hybrid winner but served_ml_kind={meta['served_ml_kind']!r} is "
                f"not a servable artifact kind {_SERVED_ML_KINDS}."
            )
        else:
            assert cv["best_ml"] == meta["served_ml_kind"], (
                f"cv.best_ml={cv['best_ml']!r} but served_ml_kind={meta['served_ml_kind']!r}. "
                f"The gate is serving a different ML variant than the one that won CV."
            )


class TestPromotionDecisionRegimeAware:
    """Regression tests for the REGIME-AWARE promotion guardrail (model.py).

    The old guardrail compared a new candidate's absolute served-MAE against the
    live version's RECORDED served-MAE. That is invalid across a data-regime
    change (the HARTI backfill grew the corpus ~1 -> ~10 seasonal cycles, which
    changed the walk-forward test distribution and the absolute MAE scale). It
    wrongly BLOCKED v8 (residual 100.51, beats every baseline on its own folds)
    because v3's crop-mean scored 94.64 on a different, easier 1-cycle regime.

    The fixed rule decides on the WITHIN-FOLD gate (beats_baseline) and never on
    cross-version absolute MAE. These tests mock the registry so they are fast
    and DB/network-free.
    """

    def _decide(self, *, ml_mae, baseline_mae, beats, cropmean_mae, live_meta):
        from unittest.mock import patch
        from agriforecast_ml.train import model
        # _promotion_decision does `from ..registry import registry` internally,
        # so patch the function on the source module.
        with patch("agriforecast_ml.registry.registry.load_promoted_metadata",
                   return_value=live_meta):
            return model._promotion_decision(
                ml_mae, baseline_mae, beats, candidate_cropmean_mae=cropmean_mae)

    def test_beating_candidate_promotes_despite_stale_lower_incumbent_mae(self):
        """THE BUG: candidate beats its own baselines -> PROMOTE, even though the
        incumbent's recorded MAE (94.64, measured on a different regime) is
        numerically lower than the candidate's MAE (100.51)."""
        live = {"version": "v3", "beats_baseline": False,
                "cv": {"cropmean_MAE": 94.64, "best_baseline_MAE": 94.64}}
        promote, reason = self._decide(
            ml_mae=100.51, baseline_mae=114.93, beats=True,
            cropmean_mae=136.13, live_meta=live)
        assert promote is True, reason
        assert "94.64" not in reason or "NOT" in reason  # must not compare to stale MAE

    def test_genuinely_worse_candidate_does_not_promote(self):
        """A non-beating candidate (serves crop-mean fallback) must NOT displace a
        live ML model. This is the 'genuinely worse' regression guard."""
        live = {"version": "v9", "beats_baseline": True,
                "cv": {"best_ml_MAE": 80.0}}
        promote, reason = self._decide(
            ml_mae=130.0, baseline_mae=120.0, beats=False,
            cropmean_mae=140.0, live_meta=live)
        assert promote is False, reason
        assert "KEEP" in reason

    def test_non_beating_candidate_keeps_non_beating_incumbent_unless_strictly_better(self):
        """Two fallbacks: keep incumbent unless the candidate's OWN-fold crop-mean
        is strictly lower (like-for-like), never on cross-regime numbers."""
        live = {"version": "v3", "beats_baseline": False,
                "cv": {"cropmean_MAE": 90.0}}
        # candidate fallback worse on its own folds -> keep incumbent
        worse, _ = self._decide(ml_mae=200.0, baseline_mae=150.0, beats=False,
                                cropmean_mae=136.13, live_meta=live)
        assert worse is False
        # candidate fallback strictly better on its own folds -> promote
        better, _ = self._decide(ml_mae=200.0, baseline_mae=150.0, beats=False,
                                 cropmean_mae=85.0, live_meta=live)
        assert better is True

    def test_bootstrap_when_nothing_promoted(self):
        promote, reason = self._decide(
            ml_mae=100.51, baseline_mae=114.93, beats=True,
            cropmean_mae=136.13, live_meta=None)
        assert promote is True
        assert "No live predictor" in reason


class TestResidualServingPath:
    """Blocker-2 regression: serving honors served_ml_kind='residual' with the
    persisted per-crop offset (expm1(model_pred + log1p(offset))), and fails
    loud + safe for any non-servable ML kind. Pure-payload tests, no DB."""

    def _payload(self, **over):
        # minimal residual payload mirroring what train_and_register persists
        base = {
            "feature_cols": [], "categorical": ["CropId"], "log_target": True,
            "quantiles": {"p10": 0.1, "p50": 0.5, "p90": 0.9},
            "fallback": {"per_crop": {}, "global": {"p10": 1, "p50": 2, "p90": 3}},
            "beats_baseline": True, "served_ml_kind": "residual",
            "residual_models": {"p10": object(), "p50": object(), "p90": object()},
            "residual_offsets": {"abc": 100.0}, "residual_offset_global": 50.0,
            "models": {"p10": object(), "p50": object(), "p90": object()},
        }
        base.update(over)
        return base

    def test_residual_offset_lookup_point_in_time(self):
        import agriforecast_ml.serving.predict as P
        old = P._PAYLOAD
        try:
            P._PAYLOAD = self._payload()
            assert P._residual_offset("abc") == 100.0       # known crop
            assert P._residual_offset("ABC") == 100.0       # case-insensitive
            assert P._residual_offset("unknown") == 50.0    # global fallback
        finally:
            P._PAYLOAD = old

    def test_servable_true_for_complete_residual_payload(self):
        import agriforecast_ml.serving.predict as P
        old = P._PAYLOAD
        try:
            P._PAYLOAD = self._payload()
            assert P._ml_servable() is True
        finally:
            P._PAYLOAD = old

    def test_guard_blocks_unknown_kind(self):
        import agriforecast_ml.serving.predict as P
        old = P._PAYLOAD
        try:
            P._PAYLOAD = self._payload(served_ml_kind="blend")
            assert P._ml_servable() is False
        finally:
            P._PAYLOAD = old

    def test_guard_blocks_residual_with_missing_artifacts(self):
        import agriforecast_ml.serving.predict as P
        old = P._PAYLOAD
        try:
            pl = self._payload()
            del pl["residual_offsets"]
            P._PAYLOAD = pl
            assert P._ml_servable() is False
        finally:
            P._PAYLOAD = old


# ===========================================================================
# 7. COLD-START FALLBACK LADDER  (P4 step 1 — ClickUp 86cahefhn)
# ===========================================================================

class TestFallbackLadder:
    """Cold-start graceful-degradation: per-crop (n_obs >= threshold) -> category
    -> global, with confidence rungs and additive response fields. Pure-payload
    tests (monkeypatch _PAYLOAD + the DB-taxonomy cache) — no DB needed for the
    ladder/confidence logic."""

    # GUIDs mapped to their DB category_code (the ladder family is now the DB
    # CropCategories.Code — the retired static gourd/fruit_veg/legume/root map is
    # dead). Brinjal -> VEG-LOW, Ginger -> VEG (grep-verified DB 2026-07-07).
    FRUITVEG_ID = "b44bacdb-042f-4286-ac23-02bc4cac4486"   # Brinjal -> VEG-LOW
    ROOT_ID     = "b725df40-9475-4254-84bd-5d0c68339a60"   # Ginger  -> VEG
    UNMAPPED_ID = "22222222-2222-2222-2222-222222222222"   # no category row -> None

    # Deterministic DB-taxonomy stub so the ladder logic stays DB-free (this class
    # tests the ladder, not the DB loader). Seeds the crop_categories cache.
    _CAT_STUB = {
        FRUITVEG_ID: "VEG-LOW",
        ROOT_ID: "VEG",
    }

    def _payload(self, per_crop=None, by_category=None, min_history=365):
        return {
            "fallback": {
                "per_crop": per_crop or {},
                "by_category": by_category or {},
                "min_history_obs": min_history,
                "global": {"p10": 10.0, "p50": 20.0, "p90": 30.0},
            },
            "beats_baseline": False,   # force the fallback path
        }

    def _patch(self, monkeypatch, payload):
        from agriforecast_ml.serving import predict as P
        from agriforecast_ml.serving import crop_categories as CC
        monkeypatch.setattr(P, "_PAYLOAD", payload)
        # Seed the DB-taxonomy cache directly (no DB round-trip).
        monkeypatch.setattr(CC, "_CROP_ID_CATEGORY", dict(self._CAT_STUB))
        return P

    def test_thick_crop_uses_own_quantiles_tier_crop(self, monkeypatch):
        """A crop with n_obs >= threshold serves its OWN quantiles (tier 'crop')."""
        pl = self._payload(per_crop={
            self.FRUITVEG_ID: {"p10": 100.0, "p50": 150.0, "p90": 200.0, "n_obs": 2600}})
        P = self._patch(monkeypatch, pl)
        q, tier = P._resolve_fallback(self.FRUITVEG_ID, "Brinjal")
        assert tier == "crop"
        assert (q["p10"], q["p50"], q["p90"]) == (100.0, 150.0, 200.0)

    def test_thin_crop_below_threshold_falls_to_category(self, monkeypatch):
        """THE presence-only bug fix: a crop present in per_crop but with n_obs BELOW
        the threshold must NOT use its own quantiles — it degrades to the category
        rung and flags degraded (Low) confidence."""
        pl = self._payload(
            per_crop={self.FRUITVEG_ID: {"p10": 1.0, "p50": 1.0, "p90": 1.0, "n_obs": 5}},
            by_category={"VEG-LOW": {"p10": 50.0, "p50": 60.0, "p90": 70.0, "n_obs": 900}})
        P = self._patch(monkeypatch, pl)
        q, tier = P._resolve_fallback(self.FRUITVEG_ID, "Brinjal")
        assert tier == "category", "thin crop must degrade past its own quantiles"
        assert (q["p10"], q["p50"], q["p90"]) == (50.0, 60.0, 70.0)
        # Fallback-served thin crop -> Low confidence (was 'Medium' under the bug).
        assert P._confidence_for(model_served=False, tier=tier) == "Low"

    def test_thin_crop_no_category_falls_to_global(self, monkeypatch):
        """Thin crop whose category has no pooled entry -> global prior (Low)."""
        pl = self._payload(
            per_crop={self.ROOT_ID: {"p10": 1.0, "p50": 1.0, "p90": 1.0, "n_obs": 5}},
            by_category={})  # no 'root' category quantiles
        P = self._patch(monkeypatch, pl)
        q, tier = P._resolve_fallback(self.ROOT_ID, "Ginger")
        assert tier == "global"
        assert (q["p10"], q["p50"], q["p90"]) == (10.0, 20.0, 30.0)

    def test_unknown_crop_falls_to_global(self, monkeypatch):
        """Unmapped/unknown crop (no per-crop, no category) -> global, Low."""
        pl = self._payload(by_category={"VEG-LOW": {"p10": 5, "p50": 6, "p90": 7, "n_obs": 9}})
        P = self._patch(monkeypatch, pl)
        q, tier = P._resolve_fallback(self.UNMAPPED_ID, None)
        assert tier == "global"
        assert P._confidence_for(model_served=False, tier=tier) == "Low"

    def test_ladder_order_full(self, monkeypatch):
        """Explicit ordering: same crop resolves crop -> category -> global as each
        higher rung is progressively removed / made ineligible."""
        # 1) thick per-crop present -> crop
        pl = self._payload(
            per_crop={self.FRUITVEG_ID: {"p10": 100, "p50": 150, "p90": 200, "n_obs": 2600}},
            by_category={"VEG-LOW": {"p10": 50, "p50": 60, "p90": 70, "n_obs": 900}})
        P = self._patch(monkeypatch, pl)
        assert P._resolve_fallback(self.FRUITVEG_ID, "Brinjal")[1] == "crop"
        # 2) thin per-crop -> category
        pl2 = self._payload(
            per_crop={self.FRUITVEG_ID: {"p10": 100, "p50": 150, "p90": 200, "n_obs": 5}},
            by_category={"VEG-LOW": {"p10": 50, "p50": 60, "p90": 70, "n_obs": 900}})
        P = self._patch(monkeypatch, pl2)
        assert P._resolve_fallback(self.FRUITVEG_ID, "Brinjal")[1] == "category"
        # 3) thin per-crop + no category -> global
        pl3 = self._payload(
            per_crop={self.FRUITVEG_ID: {"p10": 100, "p50": 150, "p90": 200, "n_obs": 5}},
            by_category={})
        P = self._patch(monkeypatch, pl3)
        assert P._resolve_fallback(self.FRUITVEG_ID, "Brinjal")[1] == "global"

    def test_configurable_threshold_via_payload(self, monkeypatch):
        """Threshold is read from the payload (configurable), not hard-coded/env.
        Lowering it lets a mid-history crop qualify as tier 'crop'."""
        per = {self.FRUITVEG_ID: {"p10": 1, "p50": 2, "p90": 3, "n_obs": 300}}
        cat = {"VEG-LOW": {"p10": 5, "p50": 6, "p90": 7, "n_obs": 9}}
        # threshold 365 -> 300 obs is thin -> category
        P = self._patch(monkeypatch, self._payload(per, cat, min_history=365))
        assert P._resolve_fallback(self.FRUITVEG_ID, "Brinjal")[1] == "category"
        # threshold 100 -> 300 obs is adequate -> crop
        P = self._patch(monkeypatch, self._payload(per, cat, min_history=100))
        assert P._resolve_fallback(self.FRUITVEG_ID, "Brinjal")[1] == "crop"

    # --- confidence rungs -------------------------------------------------
    def test_confidence_rungs(self):
        from agriforecast_ml.serving import predict as P
        assert P._confidence_for(model_served=True,  tier="crop")     == "High"
        assert P._confidence_for(model_served=False, tier="crop")     == "Medium"
        assert P._confidence_for(model_served=True,  tier="category") == "Low"
        assert P._confidence_for(model_served=False, tier="category") == "Low"
        assert P._confidence_for(model_served=True,  tier="global")   == "Low"
        assert P._confidence_for(model_served=False, tier="global")   == "Low"

    def test_low_confidence_spelling_preserved(self):
        """MlContract.cs fail-closed contract: 'Low' must keep its EXACT spelling."""
        from agriforecast_ml.serving import predict as P
        assert P._confidence_for(model_served=False, tier="global") == "Low"

    # --- DB-backed taxonomy cut-over (R2, replaces the static 4-family map) ----
    def test_category_for_reads_db_cache(self, monkeypatch):
        """category_for returns the DB category_code from the cache (no static map)."""
        from agriforecast_ml.serving import crop_categories as CC
        monkeypatch.setattr(CC, "_CROP_ID_CATEGORY",
                            {self.FRUITVEG_ID: "VEG-LOW", self.ROOT_ID: "VEG"})
        assert CC.category_for(self.FRUITVEG_ID) == "VEG-LOW"
        assert CC.category_for(self.ROOT_ID) == "VEG"
        # GUID case-insensitive.
        assert CC.category_for(self.FRUITVEG_ID.upper()) == "VEG-LOW"

    def test_category_for_fail_closed_no_row(self, monkeypatch):
        """A crop with no DB category row -> None (ladder skips to global tier),
        never a guessed family."""
        from agriforecast_ml.serving import crop_categories as CC
        monkeypatch.setattr(CC, "_CROP_ID_CATEGORY", {self.FRUITVEG_ID: "VEG-LOW"})
        assert CC.category_for(self.UNMAPPED_ID) is None
        assert CC.category_for(None) is None

    def test_category_for_empty_taxonomy_degrades(self, monkeypatch):
        """An empty/unreachable CropCategories table -> every crop resolves None
        (fail-closed), the ladder degrades per-crop -> global."""
        from agriforecast_ml.serving import crop_categories as CC
        monkeypatch.setattr(CC, "_CROP_ID_CATEGORY", {})
        assert CC.category_for(self.FRUITVEG_ID) is None


class TestNoFeatureRowForcesLowConfidence:
    """Reviewer blocker (P4 step 1): a KNOWN crop with NO scoreable CropFeatureDaily
    row as of the plant date must return confidence 'Low' — the pre-step-1 downgrade
    the fallback refactor silently dropped. On the live v10 payload (per-crop
    quantiles present, no n_obs) the tier resolves to 'crop' -> 'Medium', which would
    over-trust a genuinely un-scoreable prediction. The clamp restores 'Low' while
    fallbackTier still reports the real resolved tier.

    Pure-payload + monkeypatched DB accessors (_latest_feature_row -> None,
    _crop_meta stubbed) so no DB is needed. The schema-additivity test uses Brinjal,
    which always HAS a row, so it masks this bug — these tests must not."""

    FRUITVEG_ID = "b44bacdb-042f-4286-ac23-02bc4cac4486"   # Brinjal -> fruit_veg

    def _patch_no_row(self, monkeypatch, payload):
        from agriforecast_ml.serving import predict as P
        monkeypatch.setattr(P, "_PAYLOAD", payload)
        monkeypatch.setattr(P, "_latest_feature_row", lambda cid, pdate: None)
        # row is None -> _crop_meta is consulted; stub it so no DB is hit.
        monkeypatch.setattr(P, "_crop_meta", lambda cid: ("Brinjal", 90))
        return P

    def test_v10_shaped_payload_no_row_is_low(self, monkeypatch):
        """v10-SHAPED payload: per-crop quantiles present, NO n_obs / by_category /
        min_history_obs keys. Tier resolves to 'crop' (legacy trust), but with no
        feature row the confidence MUST be 'Low' (was 'Medium' before the fix)."""
        payload = {
            "fallback": {
                "per_crop": {self.FRUITVEG_ID: {"p10": 100.0, "p50": 150.0, "p90": 200.0}},
                "global": {"p10": 10.0, "p50": 20.0, "p90": 30.0},
            },
            "beats_baseline": False,   # force the fallback path
        }
        P = self._patch_no_row(monkeypatch, payload)
        r = P.predict_harvest(self.FRUITVEG_ID, date(2026, 1, 15))
        assert r["confidence"] == "Low", (
            f"no-feature-row prediction must be 'Low', got {r['confidence']!r}")
        assert r["activePredictor"] == "crop_mean_fallback"
        # fallbackTier still reports the REAL resolved tier (crop), not clamped.
        assert r["fallbackTier"] == "crop"
        assert "feature row" in r["confidenceReason"].lower()

    def test_new_shaped_payload_no_row_is_low(self, monkeypatch):
        """NEW-shaped payload (n_obs / by_category / min_history_obs present) with an
        adequate-history crop -> tier 'crop'. Even so, no feature row -> 'Low'."""
        payload = {
            "fallback": {
                "per_crop": {self.FRUITVEG_ID: {
                    "p10": 100.0, "p50": 150.0, "p90": 200.0, "n_obs": 2600}},
                "by_category": {"VEG-LOW": {"p10": 50.0, "p50": 60.0, "p90": 70.0, "n_obs": 900}},
                "min_history_obs": 365,
                "global": {"p10": 10.0, "p50": 20.0, "p90": 30.0},
            },
            "beats_baseline": False,
        }
        P = self._patch_no_row(monkeypatch, payload)
        r = P.predict_harvest(self.FRUITVEG_ID, date(2026, 1, 15))
        assert r["fallbackTier"] == "crop", "tier should resolve to crop (thick history)"
        assert r["confidence"] == "Low", (
            f"no-feature-row must clamp even a 'crop'-tier crop to 'Low', "
            f"got {r['confidence']!r}")
        assert r["activePredictor"] == "crop_mean_fallback"


class TestOldPayloadCompat:
    """The PROMOTED v10 payload predates these fallback keys. Serving must degrade
    gracefully when n_obs / min_history_obs / by_category are ABSENT (old-payload
    compat, like the served_ml_kind default). A retrain is NOT part of this step."""

    FRUITVEG_ID = "b44bacdb-042f-4286-ac23-02bc4cac4486"

    def test_missing_min_history_obs_uses_default(self, monkeypatch):
        from agriforecast_ml.serving import predict as P
        payload = {"fallback": {"per_crop": {}, "global": {"p10": 1, "p50": 2, "p90": 3}},
                   "beats_baseline": False}
        monkeypatch.setattr(P, "_PAYLOAD", payload)
        assert P._min_history_obs() == P._DEFAULT_MIN_HISTORY_OBS

    def test_percrop_without_n_obs_treated_as_adequate(self, monkeypatch):
        """Old per-crop entries have no n_obs key -> must keep legacy 'trust per-crop'
        behavior (tier 'crop'), never fabricate a cold-start flag."""
        from agriforecast_ml.serving import predict as P
        payload = {
            "fallback": {
                "per_crop": {self.FRUITVEG_ID: {"p10": 100, "p50": 150, "p90": 200}},
                "global": {"p10": 1, "p50": 2, "p90": 3},
            },
            "beats_baseline": False,
        }
        monkeypatch.setattr(P, "_PAYLOAD", payload)
        q, tier = P._resolve_fallback(self.FRUITVEG_ID, "Brinjal")
        assert tier == "crop"
        assert (q["p10"], q["p50"], q["p90"]) == (100, 150, 200)

    def test_missing_by_category_degrades_to_global(self, monkeypatch):
        """No by_category map (old payload) + unknown crop -> global, no crash."""
        from agriforecast_ml.serving import predict as P
        payload = {"fallback": {"per_crop": {}, "global": {"p10": 1, "p50": 2, "p90": 3}},
                   "beats_baseline": False}
        monkeypatch.setattr(P, "_PAYLOAD", payload)
        q, tier = P._resolve_fallback("33333333-3333-3333-3333-333333333333", None)
        assert tier == "global"


class TestResponseSchemaAdditivity:
    """Every pre-existing response field must still be present with unchanged
    spelling; the new fields are additive. DB-gated (calls the live payload)."""

    PRE_EXISTING_PREDICT = {
        "cropId", "cropName", "plantDate", "harvestDate", "growthPeriodDays",
        "predictedPrice", "lowerBound", "upperBound", "confidence",
        "activePredictor", "modelVersion", "explanation", "topFactors",
    }
    PRE_EXISTING_TIMELINE = {
        "cropId", "cropName", "asOf", "activePredictor", "confidence",
        "modelVersion", "explanation", "history", "forecast",
    }

    def test_predict_schema_additive(self):
        from agriforecast_ml.serving.predict import predict_harvest
        r = predict_harvest(BRINJAL_ID, PLANT_DATE)
        assert self.PRE_EXISTING_PREDICT <= set(r.keys()), (
            f"predict_harvest dropped a pre-existing field: "
            f"{self.PRE_EXISTING_PREDICT - set(r.keys())}")
        assert "confidenceReason" in r and "fallbackTier" in r
        assert r["confidence"] in {"Low", "Medium", "High"}

    def test_timeline_schema_additive(self):
        from agriforecast_ml.serving.predict import timeline
        r = timeline(BRINJAL_ID, AS_OF, 12)
        assert self.PRE_EXISTING_TIMELINE <= set(r.keys()), (
            f"timeline dropped a pre-existing field: "
            f"{self.PRE_EXISTING_TIMELINE - set(r.keys())}")
        assert "confidenceReason" in r and "fallbackTier" in r
