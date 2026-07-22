"""Per-crop readiness map (`predict.crop_readiness`) — the serving surface behind
the app's crop-status colouring (UI feature 2026-07-22).

The one property that matters: readiness must MIRROR the real serving decision,
never restate it independently. `ready` == (payload active: beats_baseline AND
_ml_servable) AND (crop passes the served_on_crops history gate, with the
legacy-payload compat rule of _is_model_served). All tests are hermetic —
monkeypatched payloads, no DB, no model artifacts, no TestClient.
"""
from __future__ import annotations

from agriforecast_ml.serving import predict

CROP_A = "aaaaaaaa-1111-2222-3333-444444444444"
CROP_B = "bbbbbbbb-1111-2222-3333-444444444444"


def _payload(**over):
    base = {
        "beats_baseline": True,
        "served_on_crops": [CROP_A],
        "fallback": {
            "min_history_obs": 365,
            "per_crop": {
                CROP_A: {"n_obs": 900, "p10": 10, "p50": 20, "p90": 30},
                CROP_B: {"n_obs": 120, "p10": 10, "p50": 20, "p90": 30},
            },
        },
    }
    base.update(over)
    return base


def _arm(monkeypatch, payload, servable=True, meta=None):
    monkeypatch.setattr(predict, "_PAYLOAD", payload)
    monkeypatch.setattr(predict, "_META", meta or {"version": "v99-test"})
    monkeypatch.setattr(predict, "_ml_servable", lambda: servable)


class TestReadyMirrorsServing:
    def test_gated_crop_ready_thin_crop_not(self, monkeypatch):
        _arm(monkeypatch, _payload())
        out = predict.crop_readiness()
        assert out["modelActive"] is True
        assert out["modelVersion"] == "v99-test"
        assert out["minHistoryObs"] == 365
        assert out["crops"][CROP_A] == {"ready": True, "nObs": 900}
        assert out["crops"][CROP_B] == {"ready": False, "nObs": 120}

    def test_inactive_payload_means_nothing_is_ready(self, monkeypatch):
        # beats_baseline=False -> serving routes EVERY crop to fallback, so the
        # map must not paint any crop green no matter the history gate.
        _arm(monkeypatch, _payload(beats_baseline=False))
        out = predict.crop_readiness()
        assert out["modelActive"] is False
        assert all(c["ready"] is False for c in out["crops"].values())

    def test_unservable_artifacts_mean_nothing_is_ready(self, monkeypatch):
        # _ml_servable()=False (promoted kind without persisted artifacts) is the
        # OTHER half of serving's model_active conjunction — same consequence.
        _arm(monkeypatch, _payload(), servable=False)
        out = predict.crop_readiness()
        assert out["modelActive"] is False
        assert all(c["ready"] is False for c in out["crops"].values())

    def test_legacy_payload_without_served_set_treats_known_crops_as_eligible(self, monkeypatch):
        # Old payloads predate served_on_crops; _is_model_served treats every crop
        # as eligible, and readiness must apply the SAME compat rule.
        p = _payload()
        del p["served_on_crops"]
        _arm(monkeypatch, p)
        out = predict.crop_readiness()
        assert all(c["ready"] is True for c in out["crops"].values())

    def test_guid_case_normalized_across_gate_and_per_crop(self, monkeypatch):
        # served_on_crops may carry uppercase GUIDs (caller case varies); the map
        # must still line the gate up with the per_crop stats on ONE lowercase key.
        p = _payload(served_on_crops=[CROP_A.upper()])
        p["fallback"]["per_crop"] = {CROP_A.upper(): {"n_obs": 900}, CROP_B: {"n_obs": 120}}
        _arm(monkeypatch, p)
        out = predict.crop_readiness()
        assert set(out["crops"]) == {CROP_A, CROP_B}
        assert out["crops"][CROP_A] == {"ready": True, "nObs": 900}


class TestDegenerateShapes:
    def test_no_payload_returns_honest_empty_shape(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD", None)
        monkeypatch.setattr(predict, "_META", None)
        out = predict.crop_readiness()
        assert out == {"modelVersion": None, "minHistoryObs": None, "modelActive": False, "crops": {}}

    def test_missing_n_obs_maps_to_none_not_zero(self, monkeypatch):
        # Pre-P4 per-crop entries have no n_obs; readiness must not fabricate a
        # count (None = "unknown", 0 would read as "no data collected").
        p = _payload()
        p["fallback"]["per_crop"][CROP_A] = {"p10": 10, "p50": 20, "p90": 30}
        _arm(monkeypatch, p)
        assert predict.crop_readiness()["crops"][CROP_A] == {"ready": True, "nObs": None}

    def test_gated_crop_absent_from_per_crop_still_listed(self, monkeypatch):
        # The crop universe is the UNION of the gate set and per_crop keys — a
        # served crop without fallback stats must not vanish from the map.
        p = _payload()
        del p["fallback"]["per_crop"][CROP_A]
        _arm(monkeypatch, p)
        assert predict.crop_readiness()["crops"][CROP_A] == {"ready": True, "nObs": None}

    def test_empty_fallback_block_survives(self, monkeypatch):
        _arm(monkeypatch, _payload(fallback=None))
        out = predict.crop_readiness()
        assert out["crops"][CROP_A]["ready"] is True
        assert out["minHistoryObs"] == predict._DEFAULT_MIN_HISTORY_OBS
