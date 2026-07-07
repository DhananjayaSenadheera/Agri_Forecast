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
