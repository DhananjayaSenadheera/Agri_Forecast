"""v14 exponential recency sample-weighting — leakage-safe tuning + math pins.

Covers the four load-bearing invariants of the v14 recency-weighting layer added
onto the v13 history-gated hybrid:

  1. Weighting math: ``w = 0.5 ** (age_days / halflife)`` — a row one half-life
     older than the reference has exactly HALF the weight of the reference row,
     and the most-recent row has weight 1.0.
  2. Point-in-time age: ages are measured from the MAX ObservationDate in the
     window passed in (never a future/global max), so weights are a pure function
     of the current window — no lookahead.
  3. Unweighted-control equivalence: ``halflife=None`` (or <=0) yields all-ones
     weights, i.e. exactly the pre-v14 fit.
  4. Tuning-never-sees-test (STRUCTURAL): the inner-split indices used by
     ``_tune_halflife`` are subsets of the train window it is given; the fold's
     test block is never passed to the tuner, so it cannot leak.

All tests are hermetic (no DB, no model artifacts).
"""
from __future__ import annotations

import numpy as np
import pandas as pd
import pytest

from agriforecast_ml.train import model as M


# 1 + 2 — weighting math and point-in-time age
class TestRecencyWeightsMath:
    def _dates(self, n, start="2024-01-01"):
        s = pd.Timestamp(start)
        return pd.Series([s + pd.Timedelta(days=i) for i in range(n)])

    def test_most_recent_row_has_weight_one(self):
        d = self._dates(10)
        w = M.recency_weights(d, halflife_days=90.0)
        # last date == max -> age 0 -> weight 1.0
        assert w[-1] == pytest.approx(1.0)
        # weights strictly increase with recency
        assert np.all(np.diff(w) > 0)

    def test_one_halflife_older_is_exactly_half(self):
        hl = 30.0
        # two rows: reference (day 30) and one half-life older (day 0)
        d = pd.Series([pd.Timestamp("2024-01-01"), pd.Timestamp("2024-01-31")])
        w = M.recency_weights(d, halflife_days=hl)
        # day 0 is 30 days older than day 30 == exactly one half-life
        assert w[1] == pytest.approx(1.0)           # reference (most recent)
        assert w[0] == pytest.approx(0.5)           # one half-life older
        assert w[0] / w[1] == pytest.approx(0.5)

    def test_two_halflives_is_a_quarter(self):
        hl = 30.0
        d = pd.Series([pd.Timestamp("2024-01-01"),   # 60d older -> 2 half-lives
                       pd.Timestamp("2024-03-01")])   # reference
        w = M.recency_weights(d, halflife_days=hl)
        assert w[0] / w[1] == pytest.approx(0.25, abs=1e-9)

    def test_halflife_ratio_scales(self):
        # A shorter half-life decays faster: at a fixed age, 90d weight > 180d? No
        # -- shorter halflife means MORE decay, so 90d weight < 180d weight for an
        # OLD row. Pin the direction explicitly.
        d = pd.Series([pd.Timestamp("2024-01-01"), pd.Timestamp("2024-07-01")])
        w90 = M.recency_weights(d, halflife_days=90.0)
        w180 = M.recency_weights(d, halflife_days=180.0)
        assert w90[0] < w180[0]   # old row decays more under the shorter halflife
        assert w90[1] == w180[1] == pytest.approx(1.0)

    def test_explicit_t_ref_overrides_window_max(self):
        d = pd.Series([pd.Timestamp("2024-01-01"), pd.Timestamp("2024-01-31")])
        # t_ref one halflife AFTER the window max -> even the newest row decays.
        w = M.recency_weights(d, halflife_days=30.0,
                              t_ref=pd.Timestamp("2024-03-01"))
        # newest row (Jan-31) is 30d before t_ref -> weight 0.5
        assert w[1] == pytest.approx(0.5)
        assert w[0] == pytest.approx(0.25)

    def test_point_in_time_no_future_max(self):
        """Weights on a train slice must be identical whether or not FUTURE rows
        exist — computed from the slice's own max, never a global/future max."""
        full = self._dates(200)
        train = full[full < pd.Timestamp("2024-04-01")]   # a prefix slice
        w_from_slice = M.recency_weights(train, halflife_days=60.0)
        # Recompute using the slice's OWN max as t_ref -> must match exactly.
        w_ref = M.recency_weights(train, halflife_days=60.0,
                                  t_ref=train.max())
        assert np.allclose(w_from_slice, w_ref)
        # The newest row of the SLICE (not the full series) has weight 1.0; the
        # full series' later rows never influence the slice's weights.
        assert w_from_slice[np.argmax(train.to_numpy())] == pytest.approx(1.0)
        assert w_from_slice.max() == pytest.approx(1.0)


# 3 — unweighted-control equivalence
class TestUnweightedControl:
    def test_none_halflife_is_all_ones(self):
        d = pd.Series([pd.Timestamp("2024-01-01") + pd.Timedelta(days=i)
                       for i in range(50)])
        w = M.recency_weights(d, halflife_days=None)
        assert np.all(w == 1.0)
        assert len(w) == 50

    def test_nonpositive_halflife_is_all_ones(self):
        d = pd.Series([pd.Timestamp("2024-01-01"), pd.Timestamp("2024-02-01")])
        assert np.all(M.recency_weights(d, halflife_days=0.0) == 1.0)
        assert np.all(M.recency_weights(d, halflife_days=-5.0) == 1.0)

    def test_none_control_matches_unweighted_fit_equivalence(self):
        """A None-halflife weight vector is the identity weighting (all-ones).
        This is what makes 'inf' a valid control in the grid."""
        d = pd.Series([pd.Timestamp("2024-01-01") + pd.Timedelta(days=i)
                       for i in range(20)])
        w = M.recency_weights(d, halflife_days=None)
        # An all-ones vector is the identity weighting: the fit is numerically
        # equivalent to sample_weight=None up to xgboost's weighted-histogram
        # float-path jitter (measured ~0.06% pred drift — approximate, not
        # bit-identical). Assert the vector we would pass is exactly all-ones.
        assert w.tolist() == [1.0] * 20


# 4 — tuning never sees the test block (structural) + grid shape
def _synthetic_gated_frame(n=800, base="2023-01-01", n_crops=3):
    """A single-crop-pooled frame long enough for a real inner split. Prices
    trend upward so recency weighting has a direction to learn."""
    rows = []
    start = pd.Timestamp(base)
    for c in range(n_crops):
        for i in range(n):
            rows.append({
                "CropId": f"crop{c}",
                "ObservationDate": start + pd.Timedelta(days=i),
                "GrowthPeriodDays": 60,
                "LabelHarvestPrice": 100.0 + 0.2 * i + c * 5.0,
            })
    df = pd.DataFrame(rows).sort_values("ObservationDate").reset_index(drop=True)
    return df


class TestTuneHalflifeLeakageSafe:
    def test_grid_contains_control(self):
        assert None in M._HALFLIFE_GRID          # unweighted control present
        assert set(M._HALFLIFE_GRID) >= {90.0, 180.0, 365.0, 730.0, None}

    def test_tuner_returns_grid_member_and_scores(self):
        df = _synthetic_gated_frame()
        from agriforecast_ml.train import dataset
        X, y, _ = dataset.build_xy(
            df.assign(LabelAvailable=1, HarvestDate=df["ObservationDate"]))
        best, scores = M._tune_halflife(df, X, y)
        assert best in M._HALFLIFE_GRID
        # every grid value scored (unless degenerate -> handled separately)
        assert set(scores.keys()) == set(M._HALFLIFE_GRID)
        assert all(isinstance(v, float) for v in scores.values())

    def test_tuner_inner_split_is_subset_of_train(self, monkeypatch):
        """STRUCTURAL leakage guard: capture the row indices the tuner fits/scores
        on and assert they are all within the train frame's index — the tuner is
        only ever handed the train window, so it CANNOT touch a fold's test block.
        We also assert the inner-val block is strictly the most-recent tail."""
        df = _synthetic_gated_frame()
        from agriforecast_ml.train import dataset
        X, y, _ = dataset.build_xy(
            df.assign(LabelAvailable=1, HarvestDate=df["ObservationDate"]))

        seen_train_idx = []
        seen_val_idx = []
        orig = M._fit_predict_log

        def spy(model, X_tr, y_tr, X_te, sample_weight=None):
            seen_train_idx.append(set(X_tr.index))
            seen_val_idx.append(set(X_te.index))
            return orig(model, X_tr, y_tr, X_te, sample_weight=sample_weight)

        monkeypatch.setattr(M, "_fit_predict_log", spy)
        M._tune_halflife(df, X, y)

        all_idx = set(df.index)
        for tr in seen_train_idx:
            assert tr <= all_idx        # inner-train ⊆ the train frame
        for va in seen_val_idx:
            assert va <= all_idx        # inner-val  ⊆ the train frame
        # inner-train and inner-val are disjoint (purged split), and the val block
        # is the recent tail (max val date > max train date).
        for tr, va in zip(seen_train_idx, seen_val_idx):
            assert tr.isdisjoint(va)
            tr_max = df.loc[list(tr), "ObservationDate"].max()
            va_min = df.loc[list(va), "ObservationDate"].min()
            assert va_min >= tr_max     # val is strictly after train (recent tail)

    def test_degenerate_inner_split_returns_control(self):
        """Too few rows/dates -> tuner returns (None, {}) so the caller uses the
        unweighted control (honest degrade, no crash)."""
        df = _synthetic_gated_frame(n=5, n_crops=1)   # far too small
        from agriforecast_ml.train import dataset
        X, y, _ = dataset.build_xy(
            df.assign(LabelAvailable=1, HarvestDate=df["ObservationDate"]))
        best, scores = M._tune_halflife(df, X, y)
        assert best is None and scores == {}


# 5 — incumbent gate: brick-safe no-op + honest promotion story (reviewer F3)
class TestIncumbentGate:
    def test_no_promoted_model_is_a_noop_never_bricks(self, monkeypatch):
        """No promoted model (or an old payload without cv.hybrid_MAE) must make
        the gate a NO-OP (beats_incumbent=True), never a fail-closed state where
        nothing can ever be promoted."""
        monkeypatch.setattr(M, "_incumbent_hybrid_mae", lambda: None)
        beats, incumbent = M._incumbent_gate(hybrid_mae=999.0)
        assert beats is True and incumbent is None

    def test_candidate_worse_than_incumbent_fails_gate(self, monkeypatch):
        monkeypatch.setattr(M, "_incumbent_hybrid_mae", lambda: 100.31)
        beats, incumbent = M._incumbent_gate(hybrid_mae=102.48)
        assert beats is False and incumbent == 100.31

    def test_candidate_better_than_incumbent_passes_gate(self, monkeypatch):
        monkeypatch.setattr(M, "_incumbent_hybrid_mae", lambda: 100.31)
        beats, incumbent = M._incumbent_gate(hybrid_mae=99.0)
        assert beats is True and incumbent == 100.31

    def test_equal_mae_does_not_beat_incumbent(self, monkeypatch):
        """Strict <: matching the incumbent is not an improvement."""
        monkeypatch.setattr(M, "_incumbent_hybrid_mae", lambda: 100.31)
        beats, _ = M._incumbent_gate(hybrid_mae=100.31)
        assert beats is False

    def test_incumbent_gate_does_not_flip_beats_baseline_source(self):
        """The incumbent flip must NOT be reintroduced: metadata honesty requires
        beats_baseline to keep telling the true baseline story when a candidate
        merely loses to the incumbent (reviewer F1 — persisted v14 metadata
        self-contradicted). The v13 corridor flip is a separate, pre-existing
        behavior and is deliberately NOT pinned here."""
        import inspect, re
        src = inspect.getsource(M)
        flip = re.search(
            r"if\s+not\s+beats_incumbent\s*:\s*\n\s*beats_baseline\s*=\s*False", src)
        assert flip is None, (
            "incumbent-gate flip of beats_baseline reintroduced — losing to the "
            "incumbent must block promotion via the guardrail short-circuit, not "
            "by rewriting the baseline story")
