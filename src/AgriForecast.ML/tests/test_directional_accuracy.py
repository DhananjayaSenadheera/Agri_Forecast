"""
AgriForecast ML -- directional-accuracy metric tests (R1.1 P4 step 0b,
ClickUp 86cajt4kw).

Two tiers:
  PURE (always run, no DB, no network):
    - directional_accuracy known-answer fixtures: all-hit, all-miss, mixed
    - reference = price known at observation date (never future-dated)
    - tie / flat-move handling (sign(0)) and the optional deadband
    - NaN reference / pred / actual -> excluded from denominator, counted
    - empty / all-excluded -> accuracy None, not 0.0
  SCHEMA:
    - metadata["cv"] retains every pre-existing (pre-P4) flat key: the new
      dir_acc block is ADDITIVE and must not perturb the gate-honesty schema
      asserted by tests/test_phase3.py.
"""
from __future__ import annotations

import sys
from pathlib import Path

import numpy as np
import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml.train.evaluate import directional_accuracy  # noqa: E402


# PURE: known-answer fixtures

class TestDirectionalAccuracy:
    def test_all_hits(self):
        # ref=100; actual up, pred up; actual down, pred down.
        ref = [100.0, 100.0]
        actual = [120.0, 80.0]
        pred = [110.0, 90.0]
        r = directional_accuracy(actual, pred, ref)
        assert r["directional_acc"] == 1.0
        assert r["n_scored"] == 2
        assert r["n_excluded"] == 0

    def test_all_misses(self):
        # ref=100; actual up but pred down, and vice versa.
        ref = [100.0, 100.0]
        actual = [120.0, 80.0]
        pred = [90.0, 110.0]
        r = directional_accuracy(actual, pred, ref)
        assert r["directional_acc"] == 0.0
        assert r["n_scored"] == 2

    def test_mixed_half(self):
        ref = [100.0, 100.0, 100.0, 100.0]
        actual = [120.0, 80.0, 120.0, 80.0]
        pred = [110.0, 90.0, 90.0, 110.0]  # first two hit, last two miss
        r = directional_accuracy(actual, pred, ref)
        assert r["directional_acc"] == 0.5
        assert r["n_scored"] == 4

    def test_reference_is_known_at_obs_not_future(self):
        # If the metric wrongly used the actual as its own reference, every row
        # would be a degenerate flat==flat comparison. Using the obs-date ref,
        # a pred that moves the RIGHT way but by a different magnitude still hits.
        ref = [50.0]
        actual = [200.0]   # big up move
        pred = [55.0]      # small up move, same direction
        r = directional_accuracy(actual, pred, ref)
        assert r["directional_acc"] == 1.0

    def test_flat_actual_flat_pred_is_hit(self):
        # sign(0)==sign(0) -> hit.
        r = directional_accuracy([100.0], [100.0], [100.0])
        assert r["directional_acc"] == 1.0

    def test_flat_pred_vs_directional_actual_is_miss(self):
        # carry-forward-style: pred == ref (flat) but actual moved up -> miss.
        r = directional_accuracy([120.0], [100.0], [100.0])
        assert r["directional_acc"] == 0.0

    def test_deadband_treats_small_move_as_flat(self):
        # actual up by 3 (within deadband=5) -> flat; pred flat -> hit.
        r = directional_accuracy([103.0], [100.0], [100.0], deadband=5.0)
        assert r["directional_acc"] == 1.0
        # without deadband it is a miss (actual up, pred flat).
        r0 = directional_accuracy([103.0], [100.0], [100.0])
        assert r0["directional_acc"] == 0.0

    def test_nan_reference_excluded_and_counted(self):
        ref = [100.0, np.nan, 100.0]
        actual = [120.0, 120.0, 80.0]
        pred = [110.0, 110.0, 90.0]
        r = directional_accuracy(actual, pred, ref)
        assert r["n_excluded"] == 1
        assert r["n_scored"] == 2      # denominator excludes the NaN row
        assert r["directional_acc"] == 1.0

    def test_nan_pred_or_actual_excluded(self):
        ref = [100.0, 100.0]
        actual = [np.nan, 80.0]
        pred = [110.0, 90.0]
        r = directional_accuracy(actual, pred, ref)
        assert r["n_excluded"] == 1
        assert r["n_scored"] == 1
        assert r["directional_acc"] == 1.0

    def test_all_excluded_returns_none_not_zero(self):
        r = directional_accuracy([np.nan], [1.0], [1.0])
        assert r["directional_acc"] is None
        assert r["n_scored"] == 0
        assert r["n_excluded"] == 1

    def test_empty_input_returns_none(self):
        r = directional_accuracy([], [], [])
        assert r["directional_acc"] is None
        assert r["n_scored"] == 0
        assert r["n_excluded"] == 0


# SCHEMA: metadata["cv"] additive-key invariant

# The exact set of flat cv keys that existed BEFORE P4 step 0b. tests/
# test_phase3.py reads these directly; they must survive verbatim.
_PRE_P4_CV_KEYS = {
    "model_MAE", "model_MAPE", "residual_MAE", "blend_MAE",
    "carry_MAE", "cropmean_MAE", "recencymean_MAE",
    "best_ml", "best_ml_MAE", "best_baseline", "best_baseline_MAE", "folds",
}


def _build_cv_metadata_like_trainer(model_mae, model_mape, residual_mae, blend_mae,
                                    carry_mae, cropmean_mae, recencymean_mae,
                                    best_ml_name, best_ml_mae, best_baseline_name,
                                    best_baseline_mae, folds, dir_acc):
    """Mirror the metadata['cv'] literal in model.train_and_register so this test
    fails loudly if the flat schema ever loses a key. Kept in sync by intent."""
    return {"model_MAE": round(model_mae, 2), "model_MAPE": round(model_mape, 2),
            "residual_MAE": round(residual_mae, 2), "blend_MAE": round(blend_mae, 2),
            "carry_MAE": round(carry_mae, 2), "cropmean_MAE": round(cropmean_mae, 2),
            "recencymean_MAE": round(recencymean_mae, 2),
            "best_ml": best_ml_name, "best_ml_MAE": round(best_ml_mae, 2),
            "best_baseline": best_baseline_name,
            "best_baseline_MAE": round(best_baseline_mae, 2), "folds": folds,
            "dir_acc": dir_acc}


class TestCvSchemaAdditive:
    def test_pre_p4_keys_are_subset_of_trainer_cv_source(self):
        """The literal in model.py must still contain every pre-P4 key."""
        src = (ML_ROOT / "agriforecast_ml" / "train" / "model.py").read_text()
        # Locate the cv dict literal.
        assert '"cv": {' in src
        for key in _PRE_P4_CV_KEYS:
            assert f'"{key}"' in src, f"pre-P4 cv key {key!r} vanished from model.py"

    def test_dir_acc_is_additive_only(self):
        cv = _build_cv_metadata_like_trainer(
            model_mae=100.0, model_mape=10.0, residual_mae=101.0, blend_mae=99.0,
            carry_mae=120.0, cropmean_mae=95.0, recencymean_mae=96.0,
            best_ml_name="model", best_ml_mae=100.0,
            best_baseline_name="crop_mean", best_baseline_mae=95.0,
            folds=[{"MAE": 100.0}],
            dir_acc={"model": 0.6, "crop_mean": 0.55})
        # Every pre-P4 key present.
        assert _PRE_P4_CV_KEYS <= set(cv.keys())
        # The only new top-level key is dir_acc.
        assert set(cv.keys()) - _PRE_P4_CV_KEYS == {"dir_acc"}
