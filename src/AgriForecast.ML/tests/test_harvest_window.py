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

  3. The two date-answering endpoints stop agreeing. `TestForecastAgreement` pins
     that /harvest-window's point for date D and predict_harvest(crop, D) return
     the SAME p10/p50/p90 — they share ONE what-if construction (predict._whatif_rows)
     and ONE rounding rule (predict._ordered_interval). They did not always: until
     the fix, predict_harvest scored the anchor row untouched, so it returned the
     same price for every future date and directly contradicted the window panel
     a farmer had just picked the date from.

Everything else here is the honesty ladder: each gate that must return
rankable=False instead of inventing a window a farmer cannot un-plant.

No DB, no model artifacts, no network — the DB-touching helpers are monkeypatched
and the "models" are deterministic fakes.
"""
from __future__ import annotations

from datetime import date, timedelta

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
    # The festival calendar is cached process-wide (one DB read per process, see
    # predict._FESTIVAL_WINDOWS). Reset it per test or the first test to supply a
    # NON-empty calendar would leak its festivals into every later test — and
    # monkeypatching load_festivals alone would silently have no effect.
    monkeypatch.setattr(predict, "_FESTIVAL_WINDOWS", None)


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
        # The best block too — nothing renders it today, which is exactly how an
        # ordering violation would ship unnoticed.
        b = out["best"]
        assert b["lowerBound"] <= b["predictedPrice"] <= b["upperBound"]

    def test_crossed_quantiles_still_return_an_ordered_interval(self, monkeypatch):
        """Independently-fitted quantile models CAN cross on a given row. Every
        emitted interval must still be ordered — points and the best block."""
        _arm(monkeypatch, payload=_payload(models={
            "p10": _SeasonalModel(170.0),   # deliberately ABOVE p50/p90
            "p50": _SeasonalModel(120.0),
            "p90": _SeasonalModel(80.0),
        }))
        out = predict.harvest_window("c1", _AS_OF, horizon_days=90)
        assert out["rankable"] is True
        for p in out["points"]:
            assert p["lowerBound"] <= p["predictedPrice"] <= p["upperBound"]
        b = out["best"]
        assert b["lowerBound"] <= b["predictedPrice"] <= b["upperBound"]


# --------------------------------------------------------------------------- #
# The two endpoints must not contradict each other.
# --------------------------------------------------------------------------- #
class TestForecastAgreement:
    """The window panel and the forecast screen answer the SAME question.

    A farmer taps a date out of the recommended window and lands on the forecast
    screen for that date; if the two disagree, the app tells them "plant Aug 25"
    and then "Aug 25 is not recommended". The only structural defence is that both
    paths build the same what-if row and round it the same way — so these tests
    compare the two outputs directly rather than re-deriving either.
    """

    def test_predict_harvest_matches_every_window_point(self, monkeypatch):
        """THE agreement assertion: same crop, same anchor row, same date ->
        identical p10/p50/p90 (and the same harvest date) from both endpoints."""
        _arm(monkeypatch)
        out = predict.harvest_window("c1", _AS_OF, horizon_days=90)
        assert out["rankable"] is True

        for p in out["points"]:
            d = date.fromisoformat(p["plantDate"])
            r = predict.predict_harvest("c1", d)
            assert r["activePredictor"] == "model", "both must be on the ML path"
            assert (r["lowerBound"], r["predictedPrice"], r["upperBound"]) == \
                   (p["lowerBound"], p["predictedPrice"], p["upperBound"]), (
                       f"window point and /predict disagree for {p['plantDate']}: "
                       f"window={p['predictedPrice']} predict={r['predictedPrice']}")
            assert r["harvestDate"] == p["harvestDate"]

    def test_predict_harvest_varies_with_the_plant_date(self, monkeypatch):
        """The bug itself. _latest_feature_row returns the same anchor for every
        future date, so scoring it unchanged returned one price for all of them —
        only the DISPLAYED harvest date moved."""
        _arm(monkeypatch)
        prices = [predict.predict_harvest("c1", _AS_OF + timedelta(days=i))
                  ["predictedPrice"] for i in range(0, 91, 10)]
        assert len(set(prices)) == len(prices), (
            "predict_harvest is date-insensitive again — it is scoring the raw "
            "anchor row instead of the what-if row for the requested date")
        assert prices == sorted(prices), "Jan->Apr should climb the seasonal sine"

    def test_past_plant_date_is_encoded_for_that_date_not_the_anchor(self, monkeypatch):
        """A PAST date's anchor row is the newest row <= that date, which may still
        be days earlier. Recomputing the calendar for the requested date is not
        leakage (calendar/festival columns are deterministic date functions) and is
        strictly more correct than inheriting the anchor's older encoding."""
        _arm(monkeypatch)
        past = _AS_OF - timedelta(days=120)
        assert (predict.predict_harvest("c1", past)["predictedPrice"]
                != predict.predict_harvest("c1", _AS_OF)["predictedPrice"])

    def test_only_the_calendar_columns_move_the_price_freeze_holds(self, monkeypatch):
        """The freeze rule: the what-if row handed to the model must carry the
        anchor's OBSERVED columns untouched (price/weather/macro are unknowable for
        a future date) and only the date-derived ones recomputed."""
        _arm(monkeypatch)
        seen: list[dict] = []
        monkeypatch.setattr(predict, "_model_quantiles_safe",
                            lambda row, crop_id=None: seen.append(dict(row)) or
                            {"p10": 1.0, "p50": 2.0, "p90": 3.0})

        d = _AS_OF + timedelta(days=45)
        predict.predict_harvest("c1", d)
        row = seen[0]

        cal = features.calendar_features(pd.DatetimeIndex([d]))
        for col in features.CALENDAR_FEATURE_COLS:
            assert row[col] == pytest.approx(float(cal[col].iloc[0])), (
                f"{col} was not recomputed for the requested plant date")
        # Frozen: genuinely unknown for a future date, so held at last observed.
        assert row["AvgPrice"] == _ANCHOR["AvgPrice"]

    def test_shap_factors_describe_the_row_that_was_scored(self, monkeypatch):
        """topFactors must explain the SAME row the number came from, or the
        'why this forecast' list describes a different date than the price above it."""
        _arm(monkeypatch)
        scored: list[dict] = []
        explained: list[dict] = []
        monkeypatch.setattr(predict, "_model_quantiles_safe",
                            lambda row, crop_id=None: scored.append(dict(row)) or
                            {"p10": 1.0, "p50": 2.0, "p90": 3.0})
        monkeypatch.setattr(
            predict.explain, "top_factor_codes",
            lambda row, payload, top_n=4: explained.append(dict(row)) or [])

        predict.predict_harvest("c1", _AS_OF + timedelta(days=45))
        assert scored and explained
        assert scored[0] == explained[0]

    def test_timeline_h1_matches_predict_for_the_same_as_of(self, monkeypatch):
        """The other same-screen pair: ForecastUI draws the hero price from
        /predict and TimelineChart's harvest marker from /timeline's first
        forecast point, for the same crop and date, a few hundred pixels apart.
        Both now score the what-if row for as_of, and at h=1 the band scale is 1,
        so the three numbers must be identical."""
        _arm(monkeypatch)
        monkeypatch.setattr(predict, "_monthly_history",
                            lambda cid, asof, max_months=12: [])

        t = predict.timeline("c1", _AS_OF, 12)
        p = predict.predict_harvest("c1", _AS_OF)
        assert t["activePredictor"] == "model" and p["activePredictor"] == "model"

        h1 = t["forecast"][0]
        assert h1["horizonMonths"] == 1
        assert (h1["lowerBound"], h1["predictedPrice"], h1["upperBound"]) == \
               (p["lowerBound"], p["predictedPrice"], p["upperBound"]), (
                   "timeline h=1 and predict_harvest disagree for the same as_of — "
                   "the chart marker and the hero price contradict each other")

    def test_timeline_without_a_growth_period_keeps_the_raw_anchor(self, monkeypatch):
        """The festival columns are HARVEST-anchored, so with no growth period
        there is no honest date to anchor them on. Keep the pre-existing behaviour
        rather than inventing a growth period."""
        row = {k: v for k, v in _ANCHOR.items() if k != "GrowthPeriodDays"}
        _arm(monkeypatch, row=row, gp=None)
        monkeypatch.setattr(predict, "_monthly_history",
                            lambda cid, asof, max_months=12: [])
        scored: list[dict] = []
        monkeypatch.setattr(predict, "_model_quantiles_safe",
                            lambda r, crop_id=None: scored.append(dict(r)) or
                            {"p10": 1.0, "p50": 2.0, "p90": 3.0})

        t = predict.timeline("c1", _AS_OF, 12)
        assert t["activePredictor"] == "model"
        assert scored[0] == row, "the anchor row must be scored untouched"

    def test_timeline_whatif_failure_degrades_like_predict(self, monkeypatch):
        """Same rule as predict_harvest: if as_of cannot be encoded we do NOT
        quietly score the anchor's stale calendar — we take the fallback ladder
        with the ml_failed clamp, so the two endpoints stay in step even here."""
        def _boom(*a, **k):
            raise RuntimeError("calendar build exploded")

        _arm(monkeypatch)
        monkeypatch.setattr(predict, "_monthly_history",
                            lambda cid, asof, max_months=12: [])
        monkeypatch.setattr(predict, "_whatif_rows", _boom)

        t = predict.timeline("c1", _AS_OF, 12)
        p = predict.predict_harvest("c1", _AS_OF)
        assert t["activePredictor"] == p["activePredictor"] == "crop_mean_fallback"
        assert t["reasonCode"] == p["reasonCode"] == "ml_failed_fallback"
        assert t["confidence"] == p["confidence"] == "Low"
        assert t["forecast"][0]["predictedPrice"] == p["predictedPrice"] == 120.0

    def test_fallback_path_never_builds_a_whatif_row(self, monkeypatch):
        """The fallback ladder scores no model row at all, so it must not depend on
        the what-if construction (and must keep its own reason/confidence)."""
        def _boom(*a, **k):
            raise AssertionError("the fallback path must not build a what-if row")

        _arm(monkeypatch, model_served=False)
        monkeypatch.setattr(predict, "_whatif_rows", _boom)
        r = predict.predict_harvest("c1", _AS_OF)
        assert r["activePredictor"] == "crop_mean_fallback"
        assert r["reasonCode"] == "not_model_served"
        assert "topFactors" not in r

    def test_whatif_failure_degrades_to_the_fallback_not_a_stale_calendar(
            self, monkeypatch):
        """If the requested date cannot be encoded we must NOT quietly score the
        anchor's own (stale) calendar — that is the bug wearing a disguise. Degrade
        through the ml_failed clamp: fallback numbers, Low confidence."""
        def _boom(*a, **k):
            raise RuntimeError("calendar build exploded")

        _arm(monkeypatch)
        monkeypatch.setattr(predict, "_whatif_rows", _boom)
        r = predict.predict_harvest("c1", _AS_OF)
        assert r["activePredictor"] == "crop_mean_fallback"
        assert r["reasonCode"] == "ml_failed_fallback"
        assert r["confidence"] == "Low"
        assert r["predictedPrice"] == 120.0   # the fallback p50, not a model score


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

    def test_whatif_build_failure_is_refused_not_raised(self, monkeypatch):
        """The row BUILD must degrade exactly like the row SCORING. A raise out of
        calendar_features is the same class of fault as a predict failure — and
        /predict and /timeline both answer it with their fallback, so the sweep
        must not be the ONE endpoint that 500s for it."""
        def _boom(*a, **k):
            raise ValueError("calendar maths exploded")

        _arm(monkeypatch)
        monkeypatch.setattr(predict.features, "calendar_features", _boom)
        self._assert_refused(predict.harvest_window("c1", _AS_OF), "scoring_failed")

    def test_festival_feature_failure_is_refused_not_raised(self, monkeypatch):
        """Same for the festival half: _whatif_rows' internal guard covers the
        calendar LOAD, not the feature computation over it."""
        def _boom(*a, **k):
            raise ValueError("festival features exploded")

        _arm(monkeypatch)
        monkeypatch.setattr(predict.features, "_festival_features", _boom)
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


# --------------------------------------------------------------------------- #
# Festival-calendar caching — one DB read per process, and never cache a failure.
# --------------------------------------------------------------------------- #
class TestFestivalCalendarCache:
    _FESTIVALS = pd.DataFrame([{
        "FestivalKey": "AVURUDU", "Date": pd.Timestamp("2026-04-13"),
        "LeadUpDays": 14, "IsProvisional": False, "Source": "seed",
    }])

    def test_the_calendar_is_read_once_per_process(self, monkeypatch):
        """_whatif_rows runs on EVERY /predict and /timeline call now, so an
        uncached read would put a DB round-trip on the two hottest endpoints for a
        ~64-row table that changes a few times a year."""
        _arm(monkeypatch, festivals=self._FESTIVALS)
        loader = predict.load_festivals
        calls: list[int] = []
        monkeypatch.setattr(predict, "load_festivals",
                            lambda: calls.append(1) or loader())
        monkeypatch.setattr(predict, "_monthly_history",
                            lambda cid, asof, max_months=12: [])

        predict.predict_harvest("c1", _AS_OF)
        predict.predict_harvest("c1", _AS_OF + timedelta(days=1))
        predict.timeline("c1", _AS_OF, 12)
        predict.harvest_window("c1", _AS_OF, horizon_days=30)

        assert len(calls) == 1, (
            "the festival calendar is being re-read per request — the cache is not "
            "holding")

    def test_an_empty_calendar_is_not_cached(self, monkeypatch):
        """load_festivals() swallows DB errors and returns an empty frame, so
        'empty' is indistinguishable from 'unreachable'. Caching it would pin a
        transient outage into festival-blind forecasts until the next restart."""
        _arm(monkeypatch)  # empty calendar
        loader = predict.load_festivals
        calls: list[int] = []
        monkeypatch.setattr(predict, "load_festivals",
                            lambda: calls.append(1) or loader())

        predict.predict_harvest("c1", _AS_OF)
        predict.predict_harvest("c1", _AS_OF + timedelta(days=1))

        assert len(calls) == 2, "an empty/unreachable calendar must not be cached"

    def test_a_raising_loader_is_not_cached_either(self, monkeypatch):
        def _boom():
            raise RuntimeError("festival table unreachable")

        _arm(monkeypatch)
        calls: list[int] = []
        monkeypatch.setattr(predict, "load_festivals",
                            lambda: calls.append(1) or _boom())

        out1 = predict.harvest_window("c1", _AS_OF, horizon_days=30)
        out2 = predict.harvest_window("c1", _AS_OF, horizon_days=30)

        assert out1["rankable"] is out2["rankable"] is True  # seasonality only
        assert len(calls) == 2, "a failed calendar read must not be cached"


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
