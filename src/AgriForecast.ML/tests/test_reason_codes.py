"""API-5: machine-stable, translatable reason CODES + factor CODES.

Covers, hermetically where possible (no DB):
  * a stable reasonCode + reasonParams per distinct confidenceReason prose branch
    (parity: distinct prose <-> distinct code), and params correctness;
  * the raw-feature -> factor-code mapping table (every family + drops + prefix
    rules; every v13 promoted feature column maps or is intentionally dropped);
  * top_factor_codes aggregation, direction/sign, weight normalization, the
    'neutral' rule, top-N cap and determinism (monkeypatched SHAP);
  * fallback OMITS topFactors while emitting reasonCode/reasonParams;
  * model-served INCLUDES topFactors (code shape) with reasonCode 'model_served';
  * PROSE-UNCHANGED regression pins (confidenceReason + explanation byte-identical);
  * a DB-gated real-model check (skips when no DB) of the live SHAP factor codes.
"""
from __future__ import annotations

from datetime import date

import numpy as np
import pytest

from agriforecast_ml.serving import explain
from agriforecast_ml.serving import predict

BRINJAL_ID = "b44bacdb-042f-4286-ac23-02bc4cac4486"

# The stable factor codes the FE can render (i18n keys under factor.codes.*).
_KNOWN_FACTOR_CODES = {
    "recent_price_trend", "festival_demand", "seasonal_supply",
    "weather_monsoon", "market_conditions", "economic_conditions",
}


# 1. reasonCode per distinct prose branch + params

# (kwargs to both _reason_for and _reason_code_for) -> expected code.
_BRANCH_CASES = [
    (dict(model_served=False, tier="crop", not_model_served=True), "not_model_served"),
    (dict(model_served=False, tier="crop", ml_failed=True), "ml_failed_fallback"),
    (dict(model_served=False, tier="crop", row_missing=True), "no_recent_feature_row"),
    (dict(model_served=False, tier="global"), "cold_start_global"),
    (dict(model_served=False, tier="category"), "cold_start_category"),
    (dict(model_served=True, tier="crop"), "model_served"),
    (dict(model_served=False, tier="crop"), "adequate_history_fallback"),
]


class TestReasonCodePerBranch:
    @pytest.mark.parametrize("kwargs,expected_code", _BRANCH_CASES)
    def test_each_branch_emits_documented_code(self, kwargs, expected_code):
        code, params = predict._reason_code_for(**kwargs)
        assert code == expected_code
        assert isinstance(params, dict)

    def test_code_branch_order_mirrors_reason_for(self):
        """One code per distinct confidenceReason prose branch, no collisions:
        the set of distinct prose strings and distinct codes must be equal-sized
        and cover all 7 branches."""
        proses, codes = set(), set()
        for kwargs, _ in _BRANCH_CASES:
            proses.add(predict._reason_for(**kwargs))
            codes.add(predict._reason_code_for(**kwargs)[0])
        assert len(proses) == len(codes) == len(_BRANCH_CASES) == 7

    def test_not_model_served_params(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD",
                            {"fallback": {"min_history_obs": 365}})
        code, params = predict._reason_code_for(
            model_served=False, tier="category", not_model_served=True,
            history_obs=214)
        assert code == "not_model_served"
        assert params == {"neededHistoryObs": 365, "historyObs": 214}

    def test_not_model_served_omits_history_obs_when_unknown(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD",
                            {"fallback": {"min_history_obs": 365}})
        _, params = predict._reason_code_for(
            model_served=False, tier="crop", not_model_served=True)
        assert params == {"neededHistoryObs": 365}
        assert "historyObs" not in params

    def test_non_flag_branches_have_empty_params(self):
        for kwargs, code in _BRANCH_CASES:
            _, params = predict._reason_code_for(**kwargs)
            if code != "not_model_served":
                assert params == {}


# 2. raw feature -> factor code mapping table

class TestFactorMapping:
    @pytest.mark.parametrize("col,expected", [
        ("AvgPrice", "recent_price_trend"),
        ("Lag30", "recent_price_trend"),
        ("RollMean90", "recent_price_trend"),
        ("Momentum", "recent_price_trend"),
        ("PriceZScore90", "recent_price_trend"),
        ("HarvestInFestivalLeadup", "festival_demand"),
        ("DaysToNextFestivalAny", "festival_demand"),
        ("InLeadupAvurudu", "festival_demand"),
        ("IsFestival", "festival_demand"),
        ("MonthNum", "seasonal_supply"),
        ("SeasonMaha", "seasonal_supply"),
        ("SinDoy", "seasonal_supply"),
        ("PlantingSeasonEnc", "seasonal_supply"),
        ("WxRainfallMm", "weather_monsoon"),
        ("WxTempLag2", "weather_monsoon"),
        ("DroughtRatio", "weather_monsoon"),
        ("FloodRatio", "weather_monsoon"),
        ("MktDambullaAvgPrice", "market_conditions"),
        ("MktNuwaraLag7", "market_conditions"),
        ("SpreadVsNational", "market_conditions"),
        ("NMarketsReporting", "market_conditions"),
        ("LeaderMarketLag7", "market_conditions"),
        ("FxUsdLkr", "economic_conditions"),
        ("MacroFoodInflationYoY", "economic_conditions"),
        ("PolicyImportBanActive", "economic_conditions"),
        ("MeanSentiment", "economic_conditions"),
    ])
    def test_mapped_columns(self, col, expected):
        assert explain.factor_code_for(col) == expected

    @pytest.mark.parametrize("col", ["CropId", "GrowthPeriodDays", "HarvestWindowDays"])
    def test_dropped_columns(self, col):
        assert explain.factor_code_for(col) is None

    def test_unknown_column_drops(self):
        assert explain.factor_code_for("SomeFutureColumn") is None

    def test_prefix_rules_generalize(self):
        # A future Mkt*/Wx* column maps by prefix without a table edit.
        assert explain.factor_code_for("MktFutureMarketAvgPrice") == "market_conditions"
        assert explain.factor_code_for("WxFutureIndex") == "weather_monsoon"

    def test_every_promoted_feature_maps_or_drops(self):
        """Every column in the CURRENTLY-PROMOTED model must resolve to a known
        factor code or be intentionally dropped (None) — never crash, never an
        unknown code leak into the breakdown."""
        payload, _ = predict._PAYLOAD, predict._META
        cols = (payload or {}).get("feature_cols")
        if not cols:
            pytest.skip("no promoted payload / feature_cols")
        dropped = set()
        for col in cols:
            code = explain.factor_code_for(col)
            if code is None:
                dropped.add(col)
            else:
                assert code in _KNOWN_FACTOR_CODES, (
                    f"{col!r} mapped to unknown code {code!r}")
        # The drop-set is EXACT: an accidental unmapping (e.g. a renamed Mkt*/Wx*
        # prefix) must fail here, not silently vanish from the farmer's breakdown.
        assert dropped == {"CropId", "GrowthPeriodDays", "HarvestWindowDays"}, (
            f"unexpected dropped columns: {sorted(dropped)}")


# 3. top_factor_codes aggregation / direction / weight / determinism

class TestTopFactorCodes:
    def _stub(self, monkeypatch, cols, sv):
        monkeypatch.setattr(explain, "_shap_contributions",
                            lambda row, payload: (cols, np.asarray(sv, dtype=float)))

    def test_aggregates_by_code_and_ranks(self, monkeypatch):
        cols = ["AvgPrice", "Lag30", "MonthNum", "WxRainfallMm",
                "MktDambullaAvgPrice", "CropId", "FxUsdLkr"]
        sv = [2.0, 1.0, -0.5, -0.3, 0.4, 5.0, 0.02]
        self._stub(monkeypatch, cols, sv)
        out = explain.top_factor_codes({"x": 1}, {"y": 1}, top_n=4)
        assert [f["code"] for f in out] == [
            "recent_price_trend", "seasonal_supply", "market_conditions",
            "weather_monsoon"]
        # CropId (dropped) never appears; economic (0.02) falls outside top-4.
        codes = {f["code"] for f in out}
        assert "economic_conditions" not in codes  # top-4 cut

    def test_direction_from_net_sign(self, monkeypatch):
        cols = ["AvgPrice", "Lag30", "MonthNum", "WxRainfallMm"]
        sv = [2.0, 1.0, -0.5, -0.3]
        self._stub(monkeypatch, cols, sv)
        out = {f["code"]: f["direction"] for f in explain.top_factor_codes({}, {}, top_n=4)}
        assert out["recent_price_trend"] == "up"      # +3.0
        assert out["seasonal_supply"] == "down"       # -0.5
        assert out["weather_monsoon"] == "down"       # -0.3

    def test_weight_normalized_0_1_and_share(self, monkeypatch):
        cols = ["AvgPrice", "MonthNum", "MktDambullaAvgPrice", "WxRainfallMm"]
        sv = [3.0, -0.5, 0.4, -0.3]  # total abs = 4.2
        self._stub(monkeypatch, cols, sv)
        out = explain.top_factor_codes({}, {}, top_n=4)
        for f in out:
            assert 0.0 <= f["weight"] <= 1.0
        assert sum(f["weight"] for f in out) <= 1.0 + 1e-9
        top = out[0]
        assert top["code"] == "recent_price_trend"
        assert top["weight"] == round(3.0 / 4.2, 3)

    def test_near_zero_share_is_neutral(self, monkeypatch):
        cols = ["AvgPrice", "FxUsdLkr"]
        sv = [10.0, 0.05]  # economic share 0.05/10.05 < 0.01 -> neutral
        self._stub(monkeypatch, cols, sv)
        out = {f["code"]: f["direction"] for f in explain.top_factor_codes({}, {}, top_n=4)}
        assert out["recent_price_trend"] == "up"
        assert out["economic_conditions"] == "neutral"

    def test_top_n_cap(self, monkeypatch):
        cols = ["AvgPrice", "MonthNum", "MktDambullaAvgPrice", "WxRainfallMm",
                "FxUsdLkr", "InLeadupAvurudu"]
        sv = [6.0, 5.0, 4.0, 3.0, 2.0, 1.0]
        self._stub(monkeypatch, cols, sv)
        assert len(explain.top_factor_codes({}, {}, top_n=4)) == 4
        assert len(explain.top_factor_codes({}, {}, top_n=2)) == 2

    def test_determinism_same_input_same_output(self, monkeypatch):
        cols = ["AvgPrice", "MonthNum", "MktDambullaAvgPrice", "WxRainfallMm"]
        sv = [1.0, 1.0, 1.0, 1.0]  # deliberate ties -> deterministic tie-break by code
        self._stub(monkeypatch, cols, sv)
        a = explain.top_factor_codes({}, {}, top_n=4)
        b = explain.top_factor_codes({}, {}, top_n=4)
        assert a == b
        # tie-break is by code name (alphabetical), fully deterministic.
        assert [f["code"] for f in a] == sorted(f["code"] for f in a)

    def test_none_contrib_and_all_zero_yield_empty(self, monkeypatch):
        monkeypatch.setattr(explain, "_shap_contributions", lambda row, payload: None)
        assert explain.top_factor_codes({}, {}, top_n=4) == []
        monkeypatch.setattr(explain, "_shap_contributions",
                            lambda row, payload: (["AvgPrice"], np.array([0.0])))
        assert explain.top_factor_codes({}, {}, top_n=4) == []


# 4. predict_harvest wiring: fallback OMITS topFactors; model INCLUDES it

def _served_payload(served):
    return {"served_on_crops": served, "beats_baseline": True,
            "served_ml_kind": "model", "models": {},
            "fallback": {"per_crop": {}, "by_category": {}, "global": {},
                         "min_history_obs": 365}}


class TestPredictWiring:
    def test_fallback_omits_topfactors_but_has_reason_code(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD", _served_payload(["served-crop"]))
        monkeypatch.setattr(predict, "_META", {"version": "vtest"})
        monkeypatch.setattr(predict, "_ml_servable", lambda: True)
        monkeypatch.setattr(predict, "_latest_feature_row",
                            lambda cid, pdate: {"CropName": "Thin", "GrowthPeriodDays": 90})
        monkeypatch.setattr(predict, "_crop_meta", lambda cid: ("Thin", 90))
        monkeypatch.setattr(predict, "_resolve_fallback",
                            lambda cid, name=None: ({"p10": 1.0, "p50": 2.0, "p90": 3.0},
                                                    "category"))
        r = predict.predict_harvest("thin-crop", date(2025, 1, 15))
        assert r["activePredictor"] == "crop_mean_fallback"
        assert "topFactors" not in r          # OMITTED on the fallback path
        assert r["reasonCode"] == "not_model_served"
        assert r["reasonParams"]["neededHistoryObs"] == 365

    def test_model_served_includes_factor_codes(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD", _served_payload(["served-crop"]))
        monkeypatch.setattr(predict, "_META", {"version": "vtest"})
        monkeypatch.setattr(predict, "_ml_servable", lambda: True)
        monkeypatch.setattr(predict, "_latest_feature_row",
                            lambda cid, pdate: {"CropName": "Fat", "GrowthPeriodDays": 90})
        monkeypatch.setattr(predict, "_resolve_fallback",
                            lambda cid, name=None: ({"p10": 1.0, "p50": 2.0, "p90": 3.0},
                                                    "crop"))
        monkeypatch.setattr(predict, "_model_quantiles_safe",
                            lambda row, crop_id=None: {"p10": 10.0, "p50": 20.0, "p90": 30.0})
        monkeypatch.setattr(predict.explain, "top_factor_codes",
                            lambda row, payload, top_n=4: [
                                {"code": "recent_price_trend", "direction": "up", "weight": 0.7}])
        r = predict.predict_harvest("served-crop", date(2025, 1, 15))
        assert r["activePredictor"] == "model"
        assert r["reasonCode"] == "model_served"
        assert r["reasonParams"] == {}
        assert r["topFactors"] == [
            {"code": "recent_price_trend", "direction": "up", "weight": 0.7}]

    def test_timeline_emits_reason_code(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD", _served_payload(["served-crop"]))
        monkeypatch.setattr(predict, "_META", {"version": "vtest"})
        monkeypatch.setattr(predict, "_ml_servable", lambda: True)
        monkeypatch.setattr(predict, "_crop_meta", lambda cid: ("Thin", 90))
        monkeypatch.setattr(predict, "_monthly_history", lambda cid, asof, max_months=12: [])
        monkeypatch.setattr(predict, "_resolve_fallback",
                            lambda cid, name=None: ({"p10": 1.0, "p50": 2.0, "p90": 3.0},
                                                    "category"))
        r = predict.timeline("thin-crop", date(2025, 1, 15), 12)
        assert r["reasonCode"] == "not_model_served"
        assert "reasonParams" in r
        assert "topFactors" not in r  # timeline never emits factor breakdown


# 5. PROSE-UNCHANGED regression pins (additive change must not alter prose)

class TestProseUnchanged:
    def test_confidence_reason_strings_byte_identical(self):
        assert predict._reason_for(model_served=False, tier="crop", not_model_served=True) == (
            "This crop does not yet have enough price history for the ML model; "
            "serving a baseline prior until it accumulates more.")
        assert predict._reason_for(model_served=False, tier="crop", ml_failed=True) == (
            "The current model cannot score this crop yet (its data arrived after "
            "the model was trained); serving a fallback prior with low confidence "
            "until the next retrain.")
        assert predict._reason_for(model_served=False, tier="crop", row_missing=True) == (
            "No recent feature row is available for this crop as of the plant date; "
            "serving a fallback prior with low confidence.")
        assert predict._reason_for(model_served=False, tier="global") == (
            "No history for this crop or its category; using an overall price prior "
            "(cold start).")
        assert predict._reason_for(model_served=False, tier="category") == (
            "Too little history for this crop; using a similar-crop category prior "
            "(cold start).")
        assert predict._reason_for(model_served=True, tier="crop") == (
            "ML model served with adequate crop history.")
        assert predict._reason_for(model_served=False, tier="crop") == (
            "Adequate crop history.")

    def test_fallback_explanation_strings_byte_identical(self):
        assert predict._fallback_explanation("crop") == (
            "Based on this crop's historical harvest-price distribution. (The ML "
            "model is not yet more accurate than this baseline at current data "
            "volume.)")
        assert predict._fallback_explanation("category") == (
            "Based on a similar-crop category's historical harvest-price "
            "distribution (too little history for this crop). (The ML model is not "
            "yet more accurate than this baseline at current data volume.)")
        assert predict._fallback_explanation("global") == (
            "Based on the overall historical harvest-price distribution (no data "
            "for this crop or its category). (The ML model is not yet more accurate "
            "than this baseline at current data volume.)")


# 6. Real promoted model (DB-gated): live SHAP factor codes are well-formed

class TestRealModelFactorCodes:
    def _predict(self):
        try:
            return predict.predict_harvest(BRINJAL_ID, date(2026, 1, 15))
        except Exception as e:  # noqa: BLE001 - DB availability guard
            pytest.skip(f"DB/model unavailable: {e}")

    def test_live_factor_codes_well_formed_and_deterministic(self):
        r1 = self._predict()
        if r1["activePredictor"] not in ("model", "residual"):
            pytest.skip("promoted model does not serve this crop (fallback path)")
        assert "topFactors" in r1 and r1["topFactors"], "model path must emit topFactors"
        for f in r1["topFactors"]:
            assert set(f.keys()) == {"code", "direction", "weight"}
            assert f["code"] in _KNOWN_FACTOR_CODES
            assert f["direction"] in {"up", "down", "neutral"}
            assert 0.0 <= f["weight"] <= 1.0
        assert r1["reasonCode"] == "model_served"
        # determinism: same input -> identical factor breakdown.
        r2 = predict.predict_harvest(BRINJAL_ID, date(2026, 1, 15))
        assert r1["topFactors"] == r2["topFactors"]
