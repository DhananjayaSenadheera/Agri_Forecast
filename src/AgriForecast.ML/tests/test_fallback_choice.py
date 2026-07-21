"""Per-crop fallback-predictor SELECTION + serving (chip task_9b1cd894).

Two surfaces:
  1. train.fallback_select.select_fallback_choices — leakage-safe per-crop
     walk-forward switch selection. Tested on SYNTHETIC series where carry-forward
     provably wins (harvest price == today's price) or provably loses (harvest
     price is a stable mean, AvgPrice is noise), plus the >=30-origin and >=10%
     switch gates and the model-served scope guard.
  2. serving.predict — reading the shipped choice map and re-centring the fallback
     interval on carry-forward, with strict FAIL-CLOSED behavior (old payloads,
     unknown crops, missing/invalid price, unservable choice values) and unchanged
     confidence / reasonCode / activePredictor strings.

All hermetic: no DB, no model artifacts (category_for is monkeypatched off).
"""
from __future__ import annotations

import pickle

import numpy as np
import pandas as pd
import pytest

from agriforecast_ml.serving import predict
from agriforecast_ml.train import fallback_select


# --------------------------------------------------------------------------- #
# Synthetic labelled-frame builder for the selection logic.
# --------------------------------------------------------------------------- #
def _crop_rows(cid, code, name, dates, avg, label, gp=30):
    return pd.DataFrame({
        "CropId": cid, "CropCode": code, "CropName": name,
        "ObservationDate": dates,
        "HarvestDate": dates + pd.Timedelta(days=gp),
        "GrowthPeriodDays": gp,
        "AvgPrice": avg, "LabelHarvestPrice": label,
    })


@pytest.fixture
def synthetic_df(monkeypatch):
    """300 daily dates. Crops:
      A (carry-wins): label == AvgPrice (a rising trend) -> carry MAE 0, recency
        (lagged weighted mean) large -> big switch.
      B (recency-wins): label constant, AvgPrice noisy -> recency ~0, carry noisy
        -> NO switch.
      C (too-thin): carry-wins but present on only ~20 test origins -> below the
        30-origin floor -> NO switch (near miss).
      D (served): excluded via served_on_crops (scope guard).
    """
    # Kill the DB category lookup: every crop has no category -> global-median tier.
    monkeypatch.setattr("agriforecast_ml.serving.crop_categories.category_for",
                        lambda cid, name=None: None)
    rng = np.random.default_rng(0)
    dates = pd.date_range("2024-01-01", periods=300, freq="D")

    trend = 100 + np.arange(300) * 2.0          # rising 100 -> ~700
    A = _crop_rows("aaaa", "VEG000A", "CarryWins", dates, trend, trend)

    const = np.full(300, 250.0)
    noise = rng.normal(0, 40, 300)
    B = _crop_rows("bbbb", "VEG000B", "RecencyWins", dates, const + noise, const)

    # C: only the last 20 dates (few origins), carry-perfect.
    dC = dates[-20:]
    trendC = 500 + np.arange(20) * 3.0
    C = _crop_rows("cccc", "VEG000C", "TooThin", dC, trendC, trendC)

    # D: served crop, carry would win but it is model-served -> never considered.
    D = _crop_rows("dddd", "VEG000D", "Served", dates, trend, trend)

    return pd.concat([A, B, C, D], ignore_index=True).sort_values(
        "ObservationDate").reset_index(drop=True)


class TestSelectionLogic:
    def _run(self, df):
        # D is model-served (excluded); A/B/C are the fallback segment.
        return fallback_select.select_fallback_choices(df, served_on_crops={"dddd"})

    def test_carry_forward_winner_switches(self, synthetic_df):
        choice, table, agg = self._run(synthetic_df)
        assert choice.get("aaaa") == "carry_forward"
        rowA = next(r for r in table if r["cropId"] == "aaaa")
        assert rowA["switched"] is True
        assert rowA["delta_pct"] >= fallback_select.DEFAULT_SWITCH_MARGIN
        assert rowA["carry_forward_MAE"] < rowA["recency_mean_MAE"]

    def test_recency_winner_does_not_switch(self, synthetic_df):
        choice, table, _ = self._run(synthetic_df)
        assert "bbbb" not in choice
        rowB = next(r for r in table if r["cropId"] == "bbbb")
        assert rowB["switched"] is False
        # carry-forward is NOT >=10% better (it is worse) -> honest no-switch.
        assert rowB["carry_forward_MAE"] >= rowB["recency_mean_MAE"] * (
            1 - fallback_select.DEFAULT_SWITCH_MARGIN)

    def test_below_min_origins_does_not_switch(self, synthetic_df):
        choice, table, _ = self._run(synthetic_df)
        assert "cccc" not in choice
        rowC = next(r for r in table if r["cropId"] == "cccc")
        assert rowC["n_origins"] < fallback_select.DEFAULT_MIN_ORIGINS
        assert rowC["switched"] is False

    def test_model_served_crop_never_in_map(self, synthetic_df):
        choice, table, _ = self._run(synthetic_df)
        assert "dddd" not in choice
        assert all(r["cropId"] != "dddd" for r in table)

    def test_only_servable_challengers_shipped(self, synthetic_df):
        choice, _, _ = self._run(synthetic_df)
        assert set(choice.values()) <= {"carry_forward"}

    def test_aggregate_improves_and_no_regress(self, synthetic_df):
        _, _, agg = self._run(synthetic_df)
        assert agg["applied"] is True
        assert agg["pooled_switched_MAE"] <= agg["pooled_recmean_MAE"]
        assert agg["no_regress"] is True

    def test_null_result_when_no_crop_passes(self, monkeypatch):
        # A frame where NO crop's carry-forward beats recency-mean -> empty map,
        # applied False, a valid null outcome.
        monkeypatch.setattr("agriforecast_ml.serving.crop_categories.category_for",
                            lambda cid, name=None: None)
        rng = np.random.default_rng(1)
        dates = pd.date_range("2024-01-01", periods=300, freq="D")
        const = np.full(300, 250.0)
        df = _crop_rows("bbbb", "VEG000B", "RecencyWins", dates,
                        const + rng.normal(0, 40, 300), const)
        choice, table, agg = fallback_select.select_fallback_choices(
            df, served_on_crops=set())
        assert choice == {}
        assert agg["applied"] is False
        assert all(r["switched"] is False for r in table)

    def test_leakage_guard_seasonal_naive_long_horizon(self):
        # seasonal-naive must be NaN for gp>=365 rows (HarvestDate-365 >= obs would
        # peek forward). Build a 1-row test frame with gp=400 and assert NaN.
        d = pd.Timestamp("2025-06-01")
        test_df = pd.DataFrame({
            "CropId": ["x"], "ObservationDate": [d],
            "HarvestDate": [d + pd.Timedelta(days=400)],
            "GrowthPeriodDays": [400], "AvgPrice": [50.0],
        })
        src = pd.DataFrame({"CropId": ["x"], "ObservationDate": [d - pd.Timedelta(days=10)],
                            "AvgPrice": [42.0]})
        out = fallback_select._seasonal_naive_pred(
            pd.concat([src, test_df[["CropId", "ObservationDate", "AvgPrice"]]]),
            test_df)
        assert np.isnan(out[0])


# --------------------------------------------------------------------------- #
# Serving: reading the choice map + fail-closed re-centring.
# --------------------------------------------------------------------------- #
def _payload(choice=None, per_obs=400):
    fb = {
        "per_crop": {"cid1": {"p10": 20.0, "p50": 30.0, "p90": 45.0, "n_obs": per_obs}},
        "by_category": {},
        "global": {"p10": 10.0, "p50": 100.0, "p90": 500.0},
        "min_history_obs": 365,
    }
    if choice is not None:
        fb["choice"] = choice
    return {
        "fallback": fb, "models": {"p50": object()}, "beats_baseline": True,
        "served_ml_kind": "model", "served_on_crops": [],  # cid1 => fallback-served
        "feature_cols": [], "categorical": ["CropId"], "quantiles": {},
    }


class TestFallbackChoiceReader:
    def test_missing_key_old_payload_is_none(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice=None))
        assert predict._fallback_choice("cid1") is None

    def test_carry_forward_choice_read(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice={"cid1": "carry_forward"}))
        assert predict._fallback_choice("cid1") == "carry_forward"

    def test_unknown_crop_is_none(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice={"cid1": "carry_forward"}))
        assert predict._fallback_choice("other") is None

    def test_unservable_value_is_none(self, monkeypatch):
        # A choice value serving cannot honor (e.g. a future seasonal_naive) fails
        # closed to the incumbent, never crashes.
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice={"cid1": "seasonal_naive"}))
        assert predict._fallback_choice("cid1") is None

    def test_case_insensitive_crop_id(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice={"cid1": "carry_forward"}))
        assert predict._fallback_choice("CID1") == "carry_forward"


class TestRecenter:
    def test_keeps_spread_and_clamps_lower_at_zero(self):
        # centre below the lower half-spread -> lower bound clamped to 0, not negative.
        q = {"p10": 20.0, "p50": 30.0, "p90": 45.0}   # gaps: lower 10, upper 15
        lo, mid, hi = predict._recenter_on(q, 5.0)
        assert mid == 5.0
        assert lo == 0.0            # 5 - 10 -> clamped
        assert hi == 20.0           # 5 + 15

    def test_ordinary_recenter(self):
        q = {"p10": 20.0, "p50": 30.0, "p90": 45.0}
        lo, mid, hi = predict._recenter_on(q, 27.5)
        assert (lo, mid, hi) == (17.5, 27.5, 42.5)

    def test_spread_source_prefers_per_crop(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice={"cid1": "carry_forward"}))
        # per_crop present -> its dict, not the (wide) resolved tier fb_q.
        fb_q = {"p10": 1.0, "p50": 100.0, "p90": 999.0}
        assert predict._spread_source("cid1", fb_q)["p50"] == 30.0
        # unknown crop -> falls back to the passed tier quantiles.
        assert predict._spread_source("other", fb_q) is fb_q


class TestPredictHarvestReCentre:
    def _row(self, avg=27.5):
        return pd.Series({"CropName": "CarryCrop", "GrowthPeriodDays": 30,
                          "AvgPrice": avg})

    def _setup(self, monkeypatch, choice, cf):
        """Common wiring: fallback path (ML yields None), a row for gp resolution,
        and a stubbed carry-forward anchor ``cf`` (float or None)."""
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice=choice))
        monkeypatch.setattr(predict, "_META", {"version": "vTest"})
        monkeypatch.setattr(predict, "_latest_feature_row", lambda c, d: self._row())
        monkeypatch.setattr(predict, "_model_quantiles_safe", lambda r, c=None: None)
        monkeypatch.setattr(predict, "_carry_forward_price", lambda c, d: cf)

    def test_switched_crop_recentres_on_carry_forward(self, monkeypatch):
        from datetime import date
        self._setup(monkeypatch, {"cid1": "carry_forward"}, 27.5)
        out = predict.predict_harvest("cid1", date(2026, 3, 1))
        assert out["predictedPrice"] == 27.5                 # carry-forward centre
        assert out["lowerBound"] == 17.5 and out["upperBound"] == 42.5
        # every string field is UNCHANGED vs a normal fallback.
        assert out["confidence"] == "Low"
        assert out["activePredictor"] == "crop_mean_fallback"
        assert out["reasonCode"] == "not_model_served"

    def test_no_choice_keeps_incumbent_p50(self, monkeypatch):
        from datetime import date
        self._setup(monkeypatch, None, 27.5)
        out = predict.predict_harvest("cid1", date(2026, 3, 1))
        assert out["predictedPrice"] == 30.0                 # incumbent per-crop p50

    def test_fail_closed_when_no_scoreable_or_stale_price(self, monkeypatch):
        # _carry_forward_price returns None for a missing / non-null-less / STALE
        # (>60d) anchor -> re-centre is skipped, incumbent p50 served.
        from datetime import date
        self._setup(monkeypatch, {"cid1": "carry_forward"}, None)
        out = predict.predict_harvest("cid1", date(2026, 3, 1))
        assert out["predictedPrice"] == 30.0                 # None anchor -> incumbent

    def test_fail_closed_when_no_row(self, monkeypatch):
        from datetime import date
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice={"cid1": "carry_forward"}))
        monkeypatch.setattr(predict, "_META", {"version": "vTest"})
        monkeypatch.setattr(predict, "_latest_feature_row", lambda c, d: None)
        monkeypatch.setattr(predict, "_model_quantiles_safe", lambda r, c=None: None)
        # _crop_meta must not hit DB: supply the meta. No scoreable price either.
        monkeypatch.setattr(predict, "_crop_meta", lambda c: ("CarryCrop", 30))
        monkeypatch.setattr(predict, "_carry_forward_price", lambda c, d: None)
        out = predict.predict_harvest("cid1", date(2026, 3, 1))
        assert out["predictedPrice"] == 30.0                 # no row -> incumbent
        assert out["confidence"] == "Low"


class TestCarryForwardStaleness:
    # S1: the anchor has a 60d max-age cap (mirrors _MACRO_STALENESS_DAYS).
    def test_predicate_boundary(self):
        from datetime import date, timedelta
        as_of = date(2026, 3, 1)
        # exactly 60 days old -> fresh (inclusive); 61 days -> stale.
        fresh = pd.Timestamp(as_of - timedelta(days=60))
        stale = pd.Timestamp(as_of - timedelta(days=61))
        assert predict._within_carry_forward_staleness(fresh, as_of) is True
        assert predict._within_carry_forward_staleness(stale, as_of) is False

    def test_constant_matches_macro_convention(self):
        assert predict._CARRY_FORWARD_STALENESS_DAYS == 60

    def test_fresh_anchor_recenters_stale_falls_back_predict(self, monkeypatch):
        from datetime import date
        # fresh -> re-centre
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice={"cid1": "carry_forward"}))
        monkeypatch.setattr(predict, "_META", {"version": "vTest"})
        monkeypatch.setattr(predict, "_latest_feature_row",
                            lambda c, d: pd.Series({"CropName": "C", "GrowthPeriodDays": 30, "AvgPrice": 27.5}))
        monkeypatch.setattr(predict, "_model_quantiles_safe", lambda r, c=None: None)
        monkeypatch.setattr(predict, "_carry_forward_price", lambda c, d: 27.5)
        assert predict.predict_harvest("cid1", date(2026, 3, 1))["predictedPrice"] == 27.5
        # stale (_carry_forward_price returns None) -> incumbent
        monkeypatch.setattr(predict, "_carry_forward_price", lambda c, d: None)
        assert predict.predict_harvest("cid1", date(2026, 3, 1))["predictedPrice"] == 30.0

    def test_fresh_anchor_recenters_stale_falls_back_timeline(self, monkeypatch):
        from datetime import date
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice={"cid1": "carry_forward"}))
        monkeypatch.setattr(predict, "_META", {"version": "vTest"})
        monkeypatch.setattr(predict, "_crop_meta", lambda c: ("C", 30))
        monkeypatch.setattr(predict, "_monthly_history", lambda c, a, max_months=12: [])
        monkeypatch.setattr(predict, "_carry_forward_price", lambda c, a: 27.5)
        assert predict.timeline("cid1", date(2026, 3, 1), 6)["forecast"][0]["predictedPrice"] == 27.5
        monkeypatch.setattr(predict, "_carry_forward_price", lambda c, a: None)
        assert predict.timeline("cid1", date(2026, 3, 1), 6)["forecast"][0]["predictedPrice"] == 30.0


class TestEndpointAnchorConsistency:
    # S3: predict_harvest and timeline must agree on the anchor. Both route through
    # _carry_forward_price (last NON-NULL + 60d cap), so a NULL newest feature row
    # cannot make them disagree.
    def _wire(self, monkeypatch, cf):
        from datetime import date
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice={"cid1": "carry_forward"}))
        monkeypatch.setattr(predict, "_META", {"version": "vTest"})
        # predict_harvest: newest feature row has NULL AvgPrice (the S3 case).
        monkeypatch.setattr(predict, "_latest_feature_row",
                            lambda c, d: pd.Series({"CropName": "C", "GrowthPeriodDays": 30, "AvgPrice": None}))
        monkeypatch.setattr(predict, "_model_quantiles_safe", lambda r, c=None: None)
        monkeypatch.setattr(predict, "_crop_meta", lambda c: ("C", 30))
        monkeypatch.setattr(predict, "_monthly_history", lambda c, a, max_months=12: [])
        # both endpoints get the SAME anchor from _carry_forward_price.
        monkeypatch.setattr(predict, "_carry_forward_price", lambda c, a: cf)
        return date(2026, 3, 1)

    def test_null_latest_row_endpoints_agree_when_older_nonnull_fresh(self, monkeypatch):
        d = self._wire(monkeypatch, 27.5)   # last non-null (older) price is fresh
        p = predict.predict_harvest("cid1", d)["predictedPrice"]
        t = predict.timeline("cid1", d, 6)["forecast"][0]["predictedPrice"]
        assert p == t == 27.5

    def test_null_latest_row_endpoints_agree_when_no_scoreable(self, monkeypatch):
        d = self._wire(monkeypatch, None)   # no non-null / too stale -> incumbent both
        p = predict.predict_harvest("cid1", d)["predictedPrice"]
        t = predict.timeline("cid1", d, 6)["forecast"][0]["predictedPrice"]
        assert p == t == 30.0


class TestTimelineReCentre:
    def test_switched_crop_timeline_anchors_on_carry_forward(self, monkeypatch):
        from datetime import date
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice={"cid1": "carry_forward"}))
        monkeypatch.setattr(predict, "_META", {"version": "vTest"})
        monkeypatch.setattr(predict, "_crop_meta", lambda c: ("CarryCrop", 30))
        monkeypatch.setattr(predict, "_monthly_history", lambda c, a, max_months=12: [])
        monkeypatch.setattr(predict, "_carry_forward_price", lambda c, a: 27.5)
        out = predict.timeline("cid1", date(2026, 3, 1), 6)
        f1 = out["forecast"][0]
        assert f1["predictedPrice"] == 27.5
        assert f1["lowerBound"] == 17.5 and f1["upperBound"] == 42.5
        assert out["confidence"] == "Low"
        assert out["activePredictor"] == "crop_mean_fallback"

    def test_timeline_fail_closed_without_price(self, monkeypatch):
        from datetime import date
        monkeypatch.setattr(predict, "_PAYLOAD", _payload(choice={"cid1": "carry_forward"}))
        monkeypatch.setattr(predict, "_META", {"version": "vTest"})
        monkeypatch.setattr(predict, "_crop_meta", lambda c: ("CarryCrop", 30))
        monkeypatch.setattr(predict, "_monthly_history", lambda c, a, max_months=12: [])
        monkeypatch.setattr(predict, "_carry_forward_price", lambda c, a: None)
        out = predict.timeline("cid1", date(2026, 3, 1), 6)
        assert out["forecast"][0]["predictedPrice"] == 30.0   # incumbent p50


class TestPayloadRoundTrip:
    def test_choice_survives_pickle(self):
        p = _payload(choice={"cid1": "carry_forward"})
        r = pickle.loads(pickle.dumps({k: v for k, v in p.items() if k != "models"}))
        assert r["fallback"]["choice"] == {"cid1": "carry_forward"}

    def test_old_payload_without_choice_has_no_key(self):
        p = _payload(choice=None)
        assert "choice" not in p["fallback"]
