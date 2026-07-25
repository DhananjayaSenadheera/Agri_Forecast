"""Best harvest window — the what-if planting-date sweep (serving/predict.harvest_window).

WHAT THESE TESTS PROTECT

The feature's whole value rests on one claim: re-scoring the SAME anchor row with
DIFFERENT calendar/festival encodings produces a genuinely different price, so the
recommended window reflects seasonal + festival demand structure rather than noise.
Two ways that claim can quietly become false, both pinned here:

  1. The sweep stops varying the date columns (e.g. someone "simplifies" it back to
     re-scoring the raw anchor row). Then every candidate scores identically and the
     window is meaningless. `test_sweep_actually_varies_by_date` and the festival
     test fail loudly if that happens — the fake models read the date columns
     STRAIGHT OUT of the built frame, so a frozen column shows up as a flat curve.

  2. The calendar encoding drifts from the one training used. `TestCalendarParity`
     pins features.calendar_features against the columns build_crop_features
     actually emits, which is the reason that helper is shared rather than copied.

Everything else here is the honesty ladder: each gate that must return
rankable=False instead of inventing a window a farmer cannot un-plant.

No DB, no model artifacts, no network — the DB-touching helpers are monkeypatched
and the "models" are deterministic fakes.
"""
from __future__ import annotations

from datetime import date

import numpy as np
import pandas as pd
import pytest

from agriforecast_ml import features
from agriforecast_ml.serving import predict


# --------------------------------------------------------------------------- #
# Calendar-feature parity: the shared helper vs what the feature build emits.
# --------------------------------------------------------------------------- #
class TestCalendarParity:
    """features.calendar_features is the SINGLE definition of the date columns.
    If build_crop_features ever re-inlines them, serving would encode a candidate
    planting date differently from how training encoded an observation date — the
    exact train/serve skew build_x.py exists to prevent, just one layer up."""

    @staticmethod
    def _meta() -> pd.Series:
        return pd.Series({
            "CropName": "Xc", "CropCode": "X", "GrowthPeriodDays": 60,
            "IsVerified": True, "PlantingSeason": "Year-round",
            "HarvestWindowDays": 14,
        })

    def test_build_emits_exactly_the_shared_helper_columns(self):
        dates = pd.date_range("2025-01-01", "2025-06-30", freq="D")
        grp = pd.DataFrame({
            "CropId": ["c1"] * len(dates),
            "CropCode": ["X"] * len(dates),
            "CropName": ["Xc"] * len(dates),
            "PriceDate": dates,
            "MinPrice": np.linspace(90, 140, len(dates)),
            "MaxPrice": np.linspace(110, 160, len(dates)),
            "AvgPrice": np.linspace(100, 150, len(dates)),
        })
        built = features.build_crop_features("c1", grp, self._meta(), {}, {})
        expected = features.calendar_features(built["ObservationDate"])

        for col in features.CALENDAR_FEATURE_COLS:
            assert col in built.columns, f"{col} vanished from the feature build"
            np.testing.assert_array_equal(
                built[col].to_numpy(), expected[col].to_numpy(),
                err_msg=f"{col} drifted from features.calendar_features")

    def test_helper_is_pure_and_leap_year_safe(self):
        """Same dates in, same numbers out — and Feb 29 must not blow up the
        day-of-year cycle (365.25 is deliberate)."""
        idx = pd.DatetimeIndex(["2024-02-29", "2025-07-15", "2026-01-01"])
        a = features.calendar_features(idx)
        b = features.calendar_features(idx)
        pd.testing.assert_frame_equal(a, b)
        assert a["SinDoy"].between(-1, 1).all()
        assert a["CosDoy"].between(-1, 1).all()
        assert list(a["DayOfYear"]) == [60, 196, 1]  # leap year counted correctly
        # Maha runs Oct-Mar, so Feb and Jan are in it and July is not.
        assert list(a["SeasonMaha"]) == [1, 0, 1]


# --------------------------------------------------------------------------- #
# Fakes for the sweep.
# --------------------------------------------------------------------------- #
class _SeasonalModel:
    """Deterministic stand-in for a promoted quantile booster.

    Reads SinDoy and HarvestInFestivalLeadup out of the frame it is handed and
    returns log1p(price), so predict's expm1 inverse recovers the price exactly.
    Because it reads the frame, a sweep that failed to vary those columns yields a
    flat curve and the assertions below fail — which is the point.
    """

    def __init__(self, level: float, festival_bonus: float = 0.0):
        self.level = level
        self.festival_bonus = festival_bonus

    def predict(self, X):
        sin = pd.to_numeric(X["SinDoy"], errors="coerce").fillna(0.0).to_numpy()
        fest = (pd.to_numeric(X.get("HarvestInFestivalLeadup", 0), errors="coerce")
                .fillna(0.0).to_numpy() if "HarvestInFestivalLeadup" in X.columns
                else np.zeros(len(X)))
        return np.log1p(self.level + 40.0 * sin + self.festival_bonus * fest)


class _FlatModel:
    def __init__(self, level: float):
        self.level = level

    def predict(self, X):
        return np.full(len(X), np.log1p(self.level))


_FEATURE_COLS = ["CropId", "AvgPrice", "SinDoy", "MonthNum",
                 "HarvestInFestivalLeadup", "DaysFromHarvestToNextFestival"]

_ANCHOR = {
    "CropId": "c1",
    "CropName": "Tomato",
    "AvgPrice": 120.0,
    "GrowthPeriodDays": 60,
    "HarvestWindowDays": 14,
    # Stale calendar values from the anchor's own (past) observation date — the
    # sweep must OVERWRITE every one of these per candidate date.
    "SinDoy": 0.0,
    "MonthNum": 1,
    "HarvestInFestivalLeadup": 0,
    "DaysFromHarvestToNextFestival": 30.0,
}


def _payload(models=None, beats_baseline=True, kind="model"):
    return {
        "feature_cols": _FEATURE_COLS,
        "categorical": ["CropId"],
        "models": models or {"p10": _SeasonalModel(80.0),
                             "p50": _SeasonalModel(120.0),
                             "p90": _SeasonalModel(170.0)},
        "beats_baseline": beats_baseline,
        "served_ml_kind": kind,
    }


def _arm(monkeypatch, payload=None, *, row=_ANCHOR, servable=True,
         model_served=True, gp=60, festivals=None):
    monkeypatch.setattr(predict, "_PAYLOAD", payload or _payload())
    monkeypatch.setattr(predict, "_META", {"version": "v99-test"})
    monkeypatch.setattr(predict, "_ml_servable", lambda: servable)
    monkeypatch.setattr(predict, "_is_model_served", lambda cid: model_served)
    monkeypatch.setattr(predict, "_latest_feature_row",
                        lambda cid, d: dict(row) if row is not None else None)
    monkeypatch.setattr(predict, "_crop_meta", lambda cid: ("Tomato", gp))
    monkeypatch.setattr(predict, "_resolve_fallback",
                        lambda cid, name: ({"p10": 90.0, "p50": 120.0,
                                            "p90": 150.0}, "crop"))
    monkeypatch.setattr(
        predict, "load_festivals",
        lambda: festivals if festivals is not None else pd.DataFrame(
            columns=["FestivalKey", "Date", "LeadUpDays", "IsProvisional", "Source"]))


# Jan 1 start: day-of-year 1..91 climbs the rising quarter of the sine, so the
# best window is deterministically the LAST full window of the sweep.
_AS_OF = date(2026, 1, 1)


# --------------------------------------------------------------------------- #
# The load-bearing behaviour.
# --------------------------------------------------------------------------- #
class TestSweep:
    def test_sweep_actually_varies_by_date(self, monkeypatch):
        """The anchor row is identical for every candidate; only the calendar
        columns change. A varying curve proves the what-if construction works."""
        _arm(monkeypatch)
        out = predict.harvest_window("C1", _AS_OF, horizon_days=90)

        assert out["rankable"] is True
        assert out["reasonCode"] == "ml_served"
        assert len(out["points"]) == 91  # inclusive of both ends

        prices = [p["predictedPrice"] for p in out["points"]]
        assert len(set(prices)) > 50, "curve is flat — date columns were not varied"
        assert prices == sorted(prices), "Jan->Apr should climb the seasonal sine"

    def test_best_window_is_the_argmax_run_and_flags_match(self, monkeypatch):
        _arm(monkeypatch)
        out = predict.harvest_window("c1", _AS_OF, horizon_days=90)

        span = out["windowDays"]
        assert span == 14  # from the crop's HarvestWindowDays
        flagged = [i for i, p in enumerate(out["points"]) if p["inBestWindow"]]
        assert len(flagged) == span
        assert flagged == list(range(flagged[0], flagged[0] + span)), "not contiguous"
        # Monotonically rising curve -> the best run is the final one.
        assert flagged[-1] == len(out["points"]) - 1

        best = out["best"]
        assert best["plantStart"] == out["points"][flagged[0]]["plantDate"]
        assert best["plantEnd"] == out["points"][flagged[-1]]["plantDate"]
        # Harvest dates are the planting dates shifted by the growth period.
        assert best["harvestStart"] == out["points"][flagged[0]]["harvestDate"]
        assert (date.fromisoformat(best["harvestStart"])
                - date.fromisoformat(best["plantStart"])).days == 60

    def test_uplift_is_measured_against_the_average_date(self, monkeypatch):
        """Not against the worst date (which would inflate it) and not against
        today (whose sign would flip arbitrarily)."""
        _arm(monkeypatch)
        out = predict.harvest_window("c1", _AS_OF, horizon_days=90)

        prices = np.array([p["predictedPrice"] for p in out["points"]])
        span = out["windowDays"]
        means = np.convolve(prices, np.ones(span) / span, mode="valid")
        expected = (means.max() - prices.mean()) / prices.mean() * 100.0
        assert out["best"]["upliftPct"] == pytest.approx(expected, abs=0.15)
        assert out["best"]["upliftPct"] > 0

    def test_harvest_anchored_festival_demand_moves_the_window(self, monkeypatch):
        """A festival lead-up landing on the HARVEST date (not the planting date)
        must pull the recommended window onto the plantings that hit it."""
        festival_day = pd.Timestamp("2026-04-13")  # Avurudu
        festivals = pd.DataFrame([{
            "FestivalKey": "AVURUDU", "Date": festival_day, "LeadUpDays": 14,
            "IsProvisional": False, "Source": "seed",
        }])
        _arm(monkeypatch, payload=_payload(models={
            "p10": _SeasonalModel(80.0, festival_bonus=60.0),
            "p50": _SeasonalModel(120.0, festival_bonus=60.0),
            "p90": _SeasonalModel(170.0, festival_bonus=60.0),
        }), festivals=festivals)

        out = predict.harvest_window("c1", _AS_OF, horizon_days=90)
        assert out["rankable"] is True

        # gp=60, lead-up [Mar 30, Apr 13] -> plantings from Jan 29 to Feb 12.
        in_window = [p for p in out["points"] if p["inBestWindow"]]
        harvests = [date.fromisoformat(p["harvestDate"]) for p in in_window]
        assert all(date(2026, 3, 30) <= h <= date(2026, 4, 13) for h in harvests), (
            "the window did not land on the festival lead-up — harvest-anchored "
            "festival features are not reaching the model frame")

    def test_bounds_are_ordered_and_horizon_is_capped(self, monkeypatch):
        _arm(monkeypatch)
        out = predict.harvest_window("c1", _AS_OF, horizon_days=10_000)
        assert len(out["points"]) == predict._WINDOW_MAX_HORIZON + 1
        for p in out["points"]:
            assert p["lowerBound"] <= p["predictedPrice"] <= p["upperBound"]


# --------------------------------------------------------------------------- #
# The honesty ladder — every path that must refuse to name a window.
# --------------------------------------------------------------------------- #
class TestRefusals:
    @staticmethod
    def _assert_refused(out, code):
        assert out["rankable"] is False
        assert out["reasonCode"] == code
        assert out["points"] == []
        assert out["best"] is None
        assert out["explanation"]  # never a bare code — the UI shows this

    def test_flat_curve_is_refused_not_ranked(self, monkeypatch):
        """The single most important refusal: a predictor that returns the same
        number for every date must NOT yield a 'best' window picked from noise."""
        _arm(monkeypatch, payload=_payload(models={
            "p10": _FlatModel(90.0), "p50": _FlatModel(120.0),
            "p90": _FlatModel(150.0),
        }))
        self._assert_refused(predict.harvest_window("c1", _AS_OF), "flat_curve")

    def test_crop_outside_the_model_history_gate_is_refused(self, monkeypatch):
        _arm(monkeypatch, model_served=False)
        self._assert_refused(predict.harvest_window("c1", _AS_OF),
                             "crop_not_model_served")

    def test_unpromoted_model_is_refused(self, monkeypatch):
        _arm(monkeypatch, payload=_payload(beats_baseline=False))
        self._assert_refused(predict.harvest_window("c1", _AS_OF), "model_inactive")

    def test_unservable_artifacts_are_refused(self, monkeypatch):
        _arm(monkeypatch, servable=False)
        self._assert_refused(predict.harvest_window("c1", _AS_OF), "model_inactive")

    def test_crop_without_a_verified_growth_period_is_refused(self, monkeypatch):
        row = {k: v for k, v in _ANCHOR.items() if k != "GrowthPeriodDays"}
        _arm(monkeypatch, row=row, gp=None)
        out = predict.harvest_window("c1", _AS_OF)
        self._assert_refused(out, "no_growth_period")
        assert out["growthPeriodDays"] is None

    def test_missing_feature_row_is_refused(self, monkeypatch):
        _arm(monkeypatch, row=None)
        self._assert_refused(predict.harvest_window("c1", _AS_OF), "no_feature_row")

    def test_scoring_failure_degrades_instead_of_raising(self, monkeypatch):
        class _Boom:
            def predict(self, X):
                raise ValueError("Found a category not in the training set")

        _arm(monkeypatch, payload=_payload(models={
            "p10": _Boom(), "p50": _Boom(), "p90": _Boom(),
        }))
        self._assert_refused(predict.harvest_window("c1", _AS_OF), "scoring_failed")

    def test_missing_festival_calendar_degrades_to_seasonality_only(self, monkeypatch):
        """An unreadable calendar must not 500 the request — the seasonal signal
        alone is still a real answer, so the sweep continues festival-blind."""
        def _boom():
            raise RuntimeError("festival table unreachable")

        _arm(monkeypatch)
        monkeypatch.setattr(predict, "load_festivals", _boom)
        out = predict.harvest_window("c1", _AS_OF, horizon_days=90)
        assert out["rankable"] is True
        assert len({p["predictedPrice"] for p in out["points"]}) > 50


class TestNoModelRegistered:
    def test_raises_when_nothing_is_promoted(self, monkeypatch):
        monkeypatch.setattr(predict, "_PAYLOAD", None)
        with pytest.raises(RuntimeError):
            predict.harvest_window("c1", _AS_OF)


# --------------------------------------------------------------------------- #
# The HTTP contract the .NET layer consumes.
# --------------------------------------------------------------------------- #
class TestEndpoint:
    @staticmethod
    def _client():
        from starlette.testclient import TestClient
        from agriforecast_ml.serving.app import app
        # raise_server_exceptions=False so the route's own except-branch is what
        # we observe, not an in-process re-raise.
        return TestClient(app, raise_server_exceptions=False)

    def test_returns_the_sweep_payload(self, monkeypatch):
        from agriforecast_ml.serving import app as app_mod

        captured = {}

        def _fake(crop_id, as_of, horizon_days):
            captured.update(cropId=crop_id, asOf=as_of, horizonDays=horizon_days)
            return {"cropId": crop_id, "rankable": True, "points": [], "best": None}

        monkeypatch.setattr(app_mod.predict, "harvest_window", _fake)
        res = self._client().post("/harvest-window", json={
            "cropId": "c1", "asOf": "2026-01-01", "horizonDays": 60})

        assert res.status_code == 200
        assert res.json()["rankable"] is True
        assert captured["asOf"] == date(2026, 1, 1)
        assert captured["horizonDays"] == 60

    def test_defaults_as_of_to_today_and_horizon_to_90(self, monkeypatch):
        from agriforecast_ml.serving import app as app_mod

        captured = {}

        def _fake(crop_id, as_of, horizon_days):
            captured.update(asOf=as_of, horizonDays=horizon_days)
            return {"rankable": False, "points": [], "best": None}

        monkeypatch.setattr(app_mod.predict, "harvest_window", _fake)
        res = self._client().post("/harvest-window", json={"cropId": "c1"})

        assert res.status_code == 200
        assert captured["asOf"] == date.today()
        assert captured["horizonDays"] == 90

    def test_an_unexpected_failure_is_not_a_500(self, monkeypatch):
        """The app must never hand the farmer-facing API a 500 for this — the
        not-rankable shape is the honest degraded answer."""
        from agriforecast_ml.serving import app as app_mod

        def _boom(crop_id, as_of, horizon_days):
            raise RuntimeError("model registry exploded")

        monkeypatch.setattr(app_mod.predict, "harvest_window", _boom)
        res = self._client().post("/harvest-window", json={"cropId": "c1"})

        assert res.status_code == 200
        body = res.json()
        assert body["rankable"] is False
        assert body["reasonCode"] == "unavailable"
        assert body["best"] is None
        assert "model registry exploded" not in str(body), "leaked internals"

    def test_out_of_range_horizon_is_rejected_at_the_edge(self):
        res = self._client().post("/harvest-window",
                                  json={"cropId": "c1", "horizonDays": 5000})
        assert res.status_code == 422
