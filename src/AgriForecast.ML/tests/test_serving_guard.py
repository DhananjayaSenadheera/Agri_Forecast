"""Serving crash-guard + explain-label completeness (R2 Step 7, reviewer F2/F3).

Covers the two Step-7 serving-robustness changes:
  1. `_model_quantiles_safe` — the ML predict call may RAISE at serve time when
     the promoted model's crop set is a strict subset of the feature store's
     (XGBoost categorical encoder raises on an unseen CropId after a corpus
     widening). The guard must degrade (return None), never propagate.
  2. The confidence clamp — a guard-degraded forecast is inert-until-retrain and
     must report "Low" regardless of the resolved fallback tier, on BOTH the
     predict_harvest and timeline paths (`ml_failed` mirrors `row_missing`).
  3. `explain._LABELS` completeness for the per-market spread columns — every
     feature-safe market's Mkt<slug>{AvgPrice,Lag7} pair needs a label or SHAP
     shows farmers raw column names. Hermetic against the pinned 11-market set;
     a live-DB variant guards future market widenings.

All tests are hermetic (no DB, no model artifacts) except the final live-DB
label check, which skips when AGRI_DB_* is absent.
"""
from __future__ import annotations

import numpy as np
import pandas as pd
import pytest

from agriforecast_ml.serving import explain, predict
from agriforecast_ml.load import _market_slug


class TestModelQuantilesSafe:
    def test_predict_failure_degrades_to_none_not_raise(self, monkeypatch):
        def boom(row, crop_id=None):
            raise ValueError("Found a category not in the training set")
        monkeypatch.setattr(predict, "_model_quantiles", boom)
        assert predict._model_quantiles_safe(object(), "some-crop-id") is None

    def test_success_passes_through(self, monkeypatch):
        q = {"p10": 1.0, "p50": 2.0, "p90": 3.0}
        monkeypatch.setattr(predict, "_model_quantiles", lambda row, crop_id=None: q)
        assert predict._model_quantiles_safe(object(), "some-crop-id") is q


class TestConfidenceClamp:
    # Exact "Low" spelling is load-bearing (.NET MlContract fail-closed) — assert
    # the literal, never a normalized form.

    @pytest.mark.parametrize("tier", ["crop", "category", "global"])
    def test_ml_failed_clamps_every_tier_to_low(self, tier):
        assert predict._confidence_for(model_served=False, tier=tier,
                                       ml_failed=True) == "Low"

    @pytest.mark.parametrize("tier", ["crop", "category", "global"])
    def test_row_missing_clamp_unchanged(self, tier):
        assert predict._confidence_for(model_served=False, tier=tier,
                                       row_missing=True) == "Low"

    @pytest.mark.parametrize("tier", ["crop", "category", "global"])
    def test_not_model_served_clamps_every_tier_to_low(self, tier):
        # Explicit clamp: even if the fallback "crop" tier ever diverges from the
        # v13 history-gate threshold, a gated-out crop must never report Medium/High.
        assert predict._confidence_for(model_served=False, tier=tier,
                                       not_model_served=True) == "Low"

    def test_ordinary_fallback_tiers_unchanged(self):
        # No clamp signals -> the pre-existing rung mapping must be untouched.
        assert predict._confidence_for(model_served=False, tier="crop") == "Medium"
        assert predict._confidence_for(model_served=False, tier="category") == "Low"
        assert predict._confidence_for(model_served=False, tier="global") == "Low"
        assert predict._confidence_for(model_served=True, tier="crop") == "High"

    def test_ml_failed_reason_is_distinct_and_leak_free(self):
        reason = predict._reason_for(model_served=False, tier="crop", ml_failed=True)
        assert "cannot score this crop" in reason
        assert reason != predict._reason_for(model_served=False, tier="crop",
                                             row_missing=True)
        # farmer-facing text: no internals leaked
        for banned in ("Traceback", "XGBoost", "/Users/", "Exception"):
            assert banned not in reason


# The 11 feature-safe markets as of R2 Step 6 (Markets seed migrations
# 20260702190530 + 20260707093230). Pinned hermetically so label drift is caught
# without a DB; the live test below catches FUTURE market widenings.
_PINNED_MARKET_NAMES = [
    "Dambulla Dedicated Economic Centre",
    "Keppetipola Dedicated Economic Centre",
    "Thambuttegama Dedicated Economic Centre",
    "Pettah (HARTI wholesale)",
    "Narahenpita (HARTI retail)",
    "Kandy (HARTI wholesale)",
    "Meegoda Dedicated Economic Centre",
    "Norochchole (HARTI wholesale)",
    "Nuwara Eliya Dedicated Economic Centre",
    "Bandarawela (HARTI wholesale)",
    "Veyangoda Dedicated Economic Centre",
]


class TestExplainLabelCompleteness:
    def test_all_pinned_market_spread_columns_have_labels(self):
        missing = []
        for name in _PINNED_MARKET_NAMES:
            slug = _market_slug(name)
            for suffix in ("AvgPrice", "Lag7"):
                col = f"Mkt{slug}{suffix}"
                if col not in explain._LABELS:
                    missing.append(col)
        assert not missing, f"explain._LABELS missing entries: {missing}"

    def test_nuwara_eliya_slug_is_first_word(self):
        # Regression pin for the non-obvious slug ("Nuwara Eliya" -> "Nuwara"):
        # a label keyed "MktNuwaraEliyaAvgPrice" would silently never match.
        assert _market_slug("Nuwara Eliya Dedicated Economic Centre") == "Nuwara"
        assert "MktNuwaraAvgPrice" in explain._LABELS

    def test_live_feature_safe_slugs_all_labelled(self):
        # Live-DB variant: catches a FUTURE market widening that forgets labels.
        import os
        if not os.environ.get("AGRI_DB_HOST"):
            pytest.skip("no AGRI_DB_* in environment")
        from agriforecast_ml.load import feature_safe_market_slugs
        try:
            slugs = feature_safe_market_slugs()
        except Exception as e:  # noqa: BLE001 - live-DB availability guard
            pytest.skip(f"DB unreachable: {e}")
        missing = [f"Mkt{slug}{suffix}"
                   for slug in slugs for suffix in ("AvgPrice", "Lag7")
                   if f"Mkt{slug}{suffix}" not in explain._LABELS]
        assert not missing, f"explain._LABELS missing entries: {missing}"


# ===========================================================================
# v13 HISTORY GATE — determinism, point-in-time honesty, single 365 definition
# ===========================================================================

def _synthetic_frame(counts: dict[str, int], base="2024-01-01") -> pd.DataFrame:
    """Build a labelled frame with `counts[crop]` rows per crop, ObservationDates
    ascending so a train-cutoff slice is well-defined."""
    rows = []
    start = pd.Timestamp(base)
    for crop, n in counts.items():
        for i in range(n):
            rows.append({"CropId": crop,
                         "ObservationDate": start + pd.Timedelta(days=i),
                         "LabelHarvestPrice": 100.0 + i})
    return pd.DataFrame(rows)


class TestHistoryGateDeterminism:
    def test_gate_is_pure_row_count(self):
        from agriforecast_ml.train.model import (
            history_gated_crops, _DEFAULT_MIN_HISTORY_OBS)
        df = _synthetic_frame({"a": 400, "b": 365, "c": 364, "d": 10})
        gated = history_gated_crops(df, _DEFAULT_MIN_HISTORY_OBS)
        # >=365 in, <365 out. b is exactly at the boundary (inclusive).
        assert gated == {"a", "b"}

    def test_gate_deterministic_across_calls(self):
        from agriforecast_ml.train.model import history_gated_crops
        df = _synthetic_frame({"a": 400, "b": 100, "c": 500})
        g1 = history_gated_crops(df, 365)
        g2 = history_gated_crops(df, 365)
        assert g1 == g2 == {"a", "c"}

    def test_gate_point_in_time_no_future_rows(self):
        """The gate must count only rows visible at a cutoff — a crop that only
        crosses 365 AFTER the cutoff must NOT be gated in at the cutoff."""
        from agriforecast_ml.train.model import history_gated_crops
        df = _synthetic_frame({"late": 500})   # 500 rows spanning 500 days
        cutoff = df["ObservationDate"].min() + pd.Timedelta(days=200)  # ~201 rows visible
        visible = df[df["ObservationDate"] < cutoff]
        assert history_gated_crops(visible, 365) == set()   # <365 visible -> excluded
        # Full frame (all rows visible) -> gated in.
        assert history_gated_crops(df, 365) == {"late"}

    def test_single_365_definition(self):
        """The gate constant must be the SAME object used by _crop_fallback's
        min_history_obs — never a forked second literal."""
        from agriforecast_ml.train import model as M
        assert M._DEFAULT_MIN_HISTORY_OBS == 365
        # _crop_fallback stamps the payload threshold from the same constant.
        df = _synthetic_frame({"a": 400})
        df["CropName"] = "A"
        # _crop_fallback calls category_for -> DB; stub it out for hermeticity.
        import agriforecast_ml.serving.crop_categories as cc
        orig = cc.category_for
        cc.category_for = lambda cid, name=None: None
        try:
            fb = M._crop_fallback(df)
        finally:
            cc.category_for = orig
        assert fb["min_history_obs"] == M._DEFAULT_MIN_HISTORY_OBS

    def test_gate_literal_appears_once_in_model_source(self):
        """Guard against a re-forked 365: the bare literal must not appear as a
        second gate threshold. It may appear only in the constant definition and
        the docstring/comment — assert the constant is the single assignment."""
        from pathlib import Path
        import agriforecast_ml.train.model as M
        src = Path(M.__file__).read_text()
        # exactly one assignment of the constant
        assert src.count("_DEFAULT_MIN_HISTORY_OBS = 365") == 1


class TestServedOnCropsRouting:
    """served_on_crops payload presence + serving routing (model-served vs the
    intentional history-gate fallback, distinct from ml_failed)."""

    def _payload(self, served):
        return {"served_on_crops": served, "beats_baseline": True,
                "served_ml_kind": "model", "models": {},
                "fallback": {"per_crop": {}, "by_category": {}, "global": {},
                             "min_history_obs": 365}}

    def test_is_model_served_membership(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD",
                            self._payload(["aaa", "bbb"]))
        assert predict._is_model_served("AAA") is True   # case-insensitive
        assert predict._is_model_served("ccc") is False

    def test_missing_served_on_crops_is_legacy_all_eligible(self, monkeypatch):
        # Old payloads (v11/v12) have no served_on_crops -> every crop eligible.
        monkeypatch.setattr(predict, "_PAYLOAD",
                            {"beats_baseline": True, "served_ml_kind": "model"})
        assert predict._served_on_crops() is None
        assert predict._is_model_served("anything") is True

    def test_non_served_reason_distinct_from_ml_failed(self):
        not_served = predict._reason_for(model_served=False, tier="category",
                                         not_model_served=True)
        ml_failed = predict._reason_for(model_served=False, tier="category",
                                        ml_failed=True)
        row_missing = predict._reason_for(model_served=False, tier="category",
                                          row_missing=True)
        assert not_served != ml_failed != row_missing != not_served
        assert "enough" in not_served and "history" in not_served
        # no internals leaked to farmers
        for banned in ("Traceback", "XGBoost", "/Users/", "Exception"):
            assert banned not in not_served

    def test_non_served_crop_takes_fallback_not_model_path(self, monkeypatch):
        """A crop outside served_on_crops must NOT reach _model_quantiles_safe —
        the routing is intentional (history gate), not crash-guard driven."""
        monkeypatch.setattr(predict, "_PAYLOAD", self._payload(["served-crop"]))
        monkeypatch.setattr(predict, "_META", {"version": "vtest"})
        monkeypatch.setattr(predict, "_ml_servable", lambda: True)
        # a feature row + gp exist for the non-served crop (so it is NOT row_missing)
        fake_row = {"CropName": "Thin", "GrowthPeriodDays": 90}
        monkeypatch.setattr(predict, "_latest_feature_row",
                            lambda cid, pdate: fake_row)
        monkeypatch.setattr(predict, "_crop_meta", lambda cid: ("Thin", 90))
        monkeypatch.setattr(predict, "_resolve_fallback",
                            lambda cid, name=None: ({"p10": 1.0, "p50": 2.0, "p90": 3.0},
                                                    "category"))

        def boom(row, crop_id=None):
            raise AssertionError("model must NOT be scored for a non-served crop")
        monkeypatch.setattr(predict, "_model_quantiles_safe", boom)

        from datetime import date
        r = predict.predict_harvest("thin-crop", date(2025, 1, 15))
        assert r["activePredictor"] == "crop_mean_fallback"
        assert r["confidence"] == "Low"          # category tier -> Low
        assert "enough" in r["confidenceReason"]  # the not_model_served reason

    def test_served_crop_reaches_model_path(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD", self._payload(["served-crop"]))
        monkeypatch.setattr(predict, "_META", {"version": "vtest"})
        monkeypatch.setattr(predict, "_ml_servable", lambda: True)
        fake_row = {"CropName": "Fat", "GrowthPeriodDays": 90}
        monkeypatch.setattr(predict, "_latest_feature_row",
                            lambda cid, pdate: fake_row)
        monkeypatch.setattr(predict, "_resolve_fallback",
                            lambda cid, name=None: ({"p10": 1.0, "p50": 2.0, "p90": 3.0},
                                                    "crop"))
        monkeypatch.setattr(predict, "_model_quantiles_safe",
                            lambda row, crop_id=None: {"p10": 10.0, "p50": 20.0, "p90": 30.0})
        monkeypatch.setattr(predict.explain, "top_factors",
                            lambda row, payload, top_n=5: [{"feature": "x", "value": 1}])
        from datetime import date
        r = predict.predict_harvest("served-crop", date(2025, 1, 15))
        assert r["activePredictor"] == "model"
        assert r["confidence"] == "High"
        assert r["predictedPrice"] == 20.0
