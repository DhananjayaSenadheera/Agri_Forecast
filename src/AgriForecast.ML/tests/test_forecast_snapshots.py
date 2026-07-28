"""Forecast snapshots (PR 0b): the nightly snapshot pass, the maturing pass and
POST /admin/snapshot-forecasts.

Every test here is hermetic - no database, no model artifacts beyond the ones
serving.predict already loads at import. The DB primitives in snapshots.py exist
precisely so the passes can be driven as pure logic; the two that cannot be faked
away (the shared price-series read and the upsert's MERGE) are proved separately:
the first against a fake pd.read_sql that ACTUALLY honours the query's date
window, the second by asserting the SQL text's own guards.

What is defended, and why each one matters:

  * SERIES IDENTITY - the matured "actual" and the serving carry-forward anchor
    must be the same function object, not two similar queries. If they drift, the
    accuracy report scores the model against a price it never saw.
  * GAP WINDOW - the actual may reach back at most SNAPSHOT_MATCH_BACK_DAYS
    (== features._FFILL_LIMIT), because that is exactly how far the training
    label's forward-fill carries. One day further and we would be scoring against
    a price the label itself would have called missing.
  * FROZEN PREDICTIONS - maturing adds actual/error columns and nothing else.
  * FAILURE ISOLATION - one crop's exception costs that crop its row, nobody else's.
  * LEAKAGE - no loader or feature module may so much as name ForecastSnapshots
    or UserSales. A snapshot fed back into training is a self-referential lookahead.
"""
from __future__ import annotations

import sys
from datetime import date, timedelta
from pathlib import Path

import pandas as pd
import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import features  # noqa: E402
from agriforecast_ml.serving import predict, snapshots  # noqa: E402


# ---------------------------------------------------------------------------
# Fakes
# ---------------------------------------------------------------------------

CROP_A = "aaaaaaaa-0000-0000-0000-000000000001"
CROP_B = "bbbbbbbb-0000-0000-0000-000000000002"


def _payload(crop_id=CROP_A, *, p50=100.0, p10=80.0, p90=120.0, gp=90,
             plant_date=date(2026, 1, 1), active="model", version="v17"):
    """A /predict payload shaped exactly like predict_harvest's return value."""
    harvest = plant_date + timedelta(days=gp) if gp else None
    return {
        "cropId": crop_id,
        "cropName": "Test Crop",
        "plantDate": plant_date.isoformat(),
        "harvestDate": harvest.isoformat() if harvest else None,
        "growthPeriodDays": gp,
        "predictedPrice": p50,
        "lowerBound": p10,
        "upperBound": p90,
        "confidence": "High",
        "confidenceReason": "reason",
        "reasonCode": "model_served",
        "reasonParams": {},
        "fallbackTier": "crop",
        "activePredictor": active,
        "modelVersion": version,
        "explanation": "explanation",
    }


class FakeSnapshotStore:
    """In-memory stand-in for the ForecastSnapshots table implementing the exact
    contract of _UPSERT_SQL: insert when absent, update when present and not yet
    terminal, and leave a terminal row frozen."""

    def __init__(self):
        self.rows: dict[tuple, dict] = {}
        self.actions: list[str] = []

    def upsert(self, engine, row):
        key = (row["crop_id"], row["snapshot_date"])
        existing = self.rows.get(key)
        if existing is None:
            self.rows[key] = dict(row)
            action = "INSERT"
        elif existing["maturity_state"] in (snapshots.MATURITY_PENDING,
                                            snapshots.MATURITY_NOT_MATURABLE):
            existing.update(row)
            action = "UPDATE"
        else:
            action = "FROZEN"
        self.actions.append(action)
        return action


def _install_snapshot_fakes(monkeypatch, *, crops=None, payload_for=None,
                            reference=27.5, store=None):
    """Wire a snapshot pass with no database anywhere."""
    crops = crops if crops is not None else [{"cropId": CROP_A, "cropName": "A"}]
    store = store if store is not None else FakeSnapshotStore()
    monkeypatch.setattr(snapshots, "get_engine", lambda: object())
    monkeypatch.setattr(snapshots, "_active_crops", lambda engine: list(crops))
    monkeypatch.setattr(snapshots, "_upsert_snapshot", store.upsert)
    monkeypatch.setattr(snapshots, "_pending_rows", lambda engine, today: [])
    monkeypatch.setattr(predict, "model_info", lambda: {"version": "v17"})
    monkeypatch.setattr(predict, "_carry_forward_price",
                        lambda cid, d: reference)
    if payload_for is None:
        def payload_for(crop_id, plant_date):
            return _payload(crop_id, plant_date=plant_date)
    monkeypatch.setattr(predict, "predict_harvest", payload_for)
    return store


def _pending(row_id, crop_id, harvest, *, p50=100.0, p10=80.0, p90=120.0):
    return {"id": row_id, "cropId": crop_id, "harvestDate": harvest,
            "predictedPrice": p50, "lowerBound": p10, "upperBound": p90}


class MatureRecorder:
    def __init__(self):
        self.matured: list[dict] = []
        self.unavailable: list[str] = []

    def install(self, monkeypatch, rows):
        monkeypatch.setattr(snapshots, "get_engine", lambda: object())
        monkeypatch.setattr(snapshots, "_pending_rows", lambda engine, today: list(rows))
        monkeypatch.setattr(snapshots, "_write_maturity",
                            lambda engine, fields: self.matured.append(fields))
        monkeypatch.setattr(snapshots, "_mark_unavailable",
                            lambda engine, row_id: self.unavailable.append(row_id))


# ---------------------------------------------------------------------------
# 1. Series identity - the mature pass and the serving anchor are ONE function
# ---------------------------------------------------------------------------

class TestSeriesIdentity:
    def test_helper_is_the_same_object_for_both_callers(self, monkeypatch):
        """The strongest form of the law: replace the helper on predict and BOTH
        the serving carry-forward anchor and the maturing pass must observe the
        replacement. If either one ever grows its own query, this fails."""
        calls: list[tuple] = []

        def sentinel(crop_id, as_of, max_back_days=None):
            calls.append((crop_id, as_of, max_back_days))
            return 42.0, as_of

        monkeypatch.setattr(predict, "_last_avgprice_at_or_before", sentinel)

        # Caller 1: the serving carry-forward anchor.
        assert predict._carry_forward_price(CROP_A, date(2026, 3, 1)) == 42.0

        # Caller 2: the maturing pass.
        rec = MatureRecorder()
        harvest = date(2026, 3, 1)
        rec.install(monkeypatch, [_pending("r1", CROP_A, harvest)])
        summary, failures = snapshots._mature_pass(object(), harvest, dry_run=False)

        assert failures == 0 and summary["matured"] == 1
        assert rec.matured[0]["actual_price"] == 42.0
        # Both callers went through the sentinel, i.e. through the same function.
        assert len(calls) == 2
        assert [c[0] for c in calls] == [CROP_A, CROP_A]

    def test_mature_pass_uses_the_label_ffill_limit_as_its_back_window(self, monkeypatch):
        """The window is not an independent 5 - it is bound to the label's own
        forward-fill limit, so the actual is exactly the carry the label makes."""
        seen: list = []
        monkeypatch.setattr(
            predict, "_last_avgprice_at_or_before",
            lambda cid, d, mbd=None: (seen.append(mbd), (10.0, d))[1])
        rec = MatureRecorder()
        rec.install(monkeypatch, [_pending("r1", CROP_A, date(2026, 3, 1))])
        snapshots._mature_pass(object(), date(2026, 3, 1), dry_run=False)
        assert seen == [snapshots.SNAPSHOT_MATCH_BACK_DAYS]
        assert snapshots.SNAPSHOT_MATCH_BACK_DAYS == features._FFILL_LIMIT == 5


# ---------------------------------------------------------------------------
# 2. Gap-window edges - proved against a fake read_sql that honours the window
# ---------------------------------------------------------------------------

def _fake_read_sql_factory(table: pd.DataFrame):
    """A pd.read_sql stand-in that actually evaluates the helper's WHERE clauses
    (<= :asof, >= :floor when present, AvgPrice NOT NULL) and its TOP 1 /
    ORDER BY DESC, so the window test proves the SQL, not a canned frame."""
    def _fake_read_sql(sql, conn, params=None):
        sql_text = str(sql)
        params = params or {}
        df = table.copy()
        df = df[df["CropId"] == params["cid"]]
        df = df[df["ObservationDate"] <= pd.Timestamp(params["asof"])]
        df = df[df["AvgPrice"].notna()]
        if ":floor" in sql_text:
            assert "floor" in params, "SQL asked for a floor but bound no param"
            df = df[df["ObservationDate"] >= pd.Timestamp(params["floor"])]
        df = df.sort_values("ObservationDate", ascending=False).head(1)
        return df[["AvgPrice", "ObservationDate"]].reset_index(drop=True)
    return _fake_read_sql


class _NullConn:
    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


def _feature_table(rows):
    return pd.DataFrame(
        [{"CropId": c, "ObservationDate": pd.Timestamp(d), "AvgPrice": p}
         for c, d, p in rows])


def _install_price_series(monkeypatch, rows):
    monkeypatch.setattr(predict, "get_engine",
                        lambda: type("E", (), {"connect": lambda self: _NullConn()})())
    monkeypatch.setattr(predict.pd, "read_sql", _fake_read_sql_factory(_feature_table(rows)))


class TestGapWindowEdges:
    HARVEST = date(2026, 3, 10)

    def test_nearer_day_wins_over_the_older_one(self, monkeypatch):
        """Both -3d and -6d have a price. The -3d one must win: nearest trading
        day AT OR BEFORE the harvest date, never an average, never the older."""
        _install_price_series(monkeypatch, [
            (CROP_A, self.HARVEST - timedelta(days=3), 111.0),
            (CROP_A, self.HARVEST - timedelta(days=6), 999.0),
        ])
        hit = predict._last_avgprice_at_or_before(
            CROP_A, self.HARVEST, snapshots.SNAPSHOT_MATCH_BACK_DAYS)
        assert hit is not None
        value, observed = hit
        assert value == 111.0
        assert pd.Timestamp(observed).date() == self.HARVEST - timedelta(days=3)

    def test_only_a_six_day_old_price_is_no_match(self, monkeypatch):
        """-6d is one day outside the label's own forward-fill reach, so there is
        no honest actual. The row stays pending rather than being scored against
        a price the label would have called missing."""
        _install_price_series(monkeypatch, [
            (CROP_A, self.HARVEST - timedelta(days=6), 999.0),
        ])
        assert predict._last_avgprice_at_or_before(
            CROP_A, self.HARVEST, snapshots.SNAPSHOT_MATCH_BACK_DAYS) is None

    def test_exactly_five_days_back_still_matches(self, monkeypatch):
        """The window is inclusive at its edge - 5 days is the last day the
        label's ffill(limit=5) still carries."""
        _install_price_series(monkeypatch, [
            (CROP_A, self.HARVEST - timedelta(days=5), 77.0),
        ])
        hit = predict._last_avgprice_at_or_before(
            CROP_A, self.HARVEST, snapshots.SNAPSHOT_MATCH_BACK_DAYS)
        assert hit is not None and hit[0] == 77.0

    def test_a_price_after_the_harvest_date_is_never_used(self, monkeypatch):
        """Lookahead guard: the day after harvest is not the harvest price."""
        _install_price_series(monkeypatch, [
            (CROP_A, self.HARVEST + timedelta(days=1), 500.0),
        ])
        assert predict._last_avgprice_at_or_before(
            CROP_A, self.HARVEST, snapshots.SNAPSHOT_MATCH_BACK_DAYS) is None

    def test_unbounded_scan_when_no_window_given(self, monkeypatch):
        """The serving anchor passes no window and applies its own staleness
        rule, so the helper must not impose one by default."""
        _install_price_series(monkeypatch, [
            (CROP_A, self.HARVEST - timedelta(days=40), 33.0),
        ])
        hit = predict._last_avgprice_at_or_before(CROP_A, self.HARVEST)
        assert hit is not None and hit[0] == 33.0

    def test_carry_forward_price_behaviour_is_unchanged_by_the_refactor(self, monkeypatch):
        """The refactor must not move a live serving number: the 60-day staleness
        cap and the positive-price rule still belong to _carry_forward_price."""
        _install_price_series(monkeypatch, [
            (CROP_A, self.HARVEST - timedelta(days=30), 27.5),
            (CROP_B, self.HARVEST - timedelta(days=61), 27.5),
        ])
        assert predict._carry_forward_price(CROP_A, self.HARVEST) == 27.5
        assert predict._carry_forward_price(CROP_B, self.HARVEST) is None  # stale


# ---------------------------------------------------------------------------
# 3. pending -> actual_unavailable timing
# ---------------------------------------------------------------------------

class TestUnavailableTiming:
    HARVEST = date(2026, 3, 1)

    def _run(self, monkeypatch, today):
        monkeypatch.setattr(predict, "_last_avgprice_at_or_before",
                            lambda cid, d, mbd=None: None)
        rec = MatureRecorder()
        rec.install(monkeypatch, [_pending("r1", CROP_A, self.HARVEST)])
        summary, failures = snapshots._mature_pass(object(), today, dry_run=False)
        return rec, summary, failures

    def test_inside_the_window_the_row_stays_pending(self, monkeypatch):
        """A missing price a few days after harvest is late ingestion, not a
        verdict: retry nightly."""
        rec, summary, failures = self._run(
            monkeypatch, self.HARVEST + timedelta(days=snapshots.SNAPSHOT_UNAVAILABLE_AFTER_DAYS))
        assert rec.unavailable == [] and rec.matured == []
        assert summary == {"scanned": 1, "matured": 0, "stillPending": 1,
                           "markedUnavailable": 0, "maxHarvestDateMatured": None}
        assert failures == 0

    def test_one_day_past_the_line_is_terminal(self, monkeypatch):
        rec, summary, failures = self._run(
            monkeypatch,
            self.HARVEST + timedelta(days=snapshots.SNAPSHOT_UNAVAILABLE_AFTER_DAYS + 1))
        assert rec.unavailable == ["r1"]
        assert summary["markedUnavailable"] == 1
        assert summary["stillPending"] == 0
        assert summary["matured"] == 0
        assert failures == 0

    def test_unavailable_is_never_a_faked_actual(self, monkeypatch):
        """Giving up must not write a price. Nothing may reach _write_maturity."""
        rec, _, _ = self._run(monkeypatch, self.HARVEST + timedelta(days=60))
        assert rec.matured == []

    def test_a_non_positive_actual_is_never_scored(self, monkeypatch):
        """A zero/negative AvgPrice is not a harvest price. Scoring it would also
        blow PercentageError's column range through the 1e-6 clip, and a row that
        fails to write would retry every night forever - so it is refused and
        ages out through the normal give-up path."""
        monkeypatch.setattr(predict, "_last_avgprice_at_or_before",
                            lambda cid, d, mbd=None: (0.0, d))
        rec = MatureRecorder()
        rec.install(monkeypatch, [_pending("r1", CROP_A, self.HARVEST)])
        summary, failures = snapshots._mature_pass(
            object(), self.HARVEST, dry_run=False)
        assert rec.matured == [] and failures == 0
        assert summary["stillPending"] == 1
        # ...and it does go terminal eventually rather than retrying forever.
        rec2 = MatureRecorder()
        rec2.install(monkeypatch, [_pending("r1", CROP_A, self.HARVEST)])
        snapshots._mature_pass(object(), self.HARVEST + timedelta(days=15), dry_run=False)
        assert rec2.unavailable == ["r1"]

    def test_grace_constant_is_documented_and_ordered(self):
        """The grace constant is the latency rationale, not a branch: it must sit
        between "just missed" and the give-up line, or it describes nothing."""
        assert snapshots.SNAPSHOT_MATURE_GRACE_DAYS == 7
        assert (snapshots.SNAPSHOT_MATCH_BACK_DAYS
                < snapshots.SNAPSHOT_MATURE_GRACE_DAYS
                < snapshots.SNAPSHOT_UNAVAILABLE_AFTER_DAYS == 14)


# ---------------------------------------------------------------------------
# 3b. Do not score a carried price before the harvest day has had its chance (B1)
# ---------------------------------------------------------------------------

class TestNoEarlyMaturing:
    """The nightly pass runs while the feature store typically ends at H-1. Without
    this rule every row would freeze an H-1 price as its harvest actual on the very
    first sweep - permanently, since matured rows are frozen - and AvgPrice moves
    about 10% day over day. Maturing is irreversible, so waiting is cheap and being
    wrong is not."""

    HARVEST = date(2026, 3, 10)

    def _run(self, monkeypatch, observed, today):
        monkeypatch.setattr(predict, "_last_avgprice_at_or_before",
                            lambda cid, d, mbd=None: (90.0, observed))
        rec = MatureRecorder()
        rec.install(monkeypatch, [_pending("r1", CROP_A, self.HARVEST)])
        summary, failures = snapshots._mature_pass(object(), today, dry_run=False)
        assert failures == 0
        return rec, summary

    def test_the_harvest_days_own_price_matures_immediately(self, monkeypatch):
        rec, summary = self._run(monkeypatch, self.HARVEST, self.HARVEST)
        assert summary["matured"] == 1
        assert rec.matured[0]["actual_observed_date"] == self.HARVEST

    def test_a_carried_price_inside_the_window_leaves_the_row_pending(self, monkeypatch):
        """Only H-1 is available and the harvest day's price may still publish:
        wait, do not freeze the wrong number."""
        rec, summary = self._run(monkeypatch, self.HARVEST - timedelta(days=1),
                                 self.HARVEST)
        assert rec.matured == []
        assert summary == {"scanned": 1, "matured": 0, "stillPending": 1,
                           "markedUnavailable": 0, "maxHarvestDateMatured": None}

    def test_still_pending_one_day_before_the_grace_line(self, monkeypatch):
        rec, summary = self._run(
            monkeypatch, self.HARVEST - timedelta(days=1),
            self.HARVEST + timedelta(days=snapshots.SNAPSHOT_MATURE_GRACE_DAYS - 1))
        assert rec.matured == [] and summary["stillPending"] == 1

    def test_the_carry_is_accepted_once_the_grace_window_has_passed(self, monkeypatch):
        """The harvest day's price has had its publication window and is not
        coming; the carry is now the honest best answer - exactly what the
        label's own ffill would use."""
        rec, summary = self._run(
            monkeypatch, self.HARVEST - timedelta(days=1),
            self.HARVEST + timedelta(days=snapshots.SNAPSHOT_MATURE_GRACE_DAYS))
        assert summary["matured"] == 1
        assert rec.matured[0]["actual_observed_date"] == self.HARVEST - timedelta(days=1)

    def test_the_exact_day_wins_even_long_after_the_grace_window(self, monkeypatch):
        """Ordering-independence: the rule is a property of the dates, not of
        which branch is checked first."""
        rec, summary = self._run(monkeypatch, self.HARVEST,
                                 self.HARVEST + timedelta(days=30))
        assert summary["matured"] == 1
        assert rec.matured[0]["actual_observed_date"] == self.HARVEST

    def test_the_terminal_path_is_unchanged(self, monkeypatch):
        """No price at all past the give-up line is still actual_unavailable, and
        the give-up line still sits beyond the grace window so a row that waited
        for its exact day gets its carry considered first."""
        monkeypatch.setattr(predict, "_last_avgprice_at_or_before",
                            lambda cid, d, mbd=None: None)
        rec = MatureRecorder()
        rec.install(monkeypatch, [_pending("r1", CROP_A, self.HARVEST)])
        summary, _ = snapshots._mature_pass(
            object(),
            self.HARVEST + timedelta(days=snapshots.SNAPSHOT_UNAVAILABLE_AFTER_DAYS + 1),
            dry_run=False)
        assert rec.unavailable == ["r1"] and summary["markedUnavailable"] == 1
        assert (snapshots.SNAPSHOT_MATURE_GRACE_DAYS
                < snapshots.SNAPSHOT_UNAVAILABLE_AFTER_DAYS)

    def test_a_row_that_waited_still_matures_before_it_can_be_given_up(self, monkeypatch):
        """The two windows must not race: at the give-up line a carried price is
        long since acceptable, so a row with ANY price in reach matures rather
        than being marked unavailable."""
        rec, summary = self._run(
            monkeypatch, self.HARVEST - timedelta(days=1),
            self.HARVEST + timedelta(days=snapshots.SNAPSHOT_UNAVAILABLE_AFTER_DAYS + 1))
        assert summary["matured"] == 1 and summary["markedUnavailable"] == 0
        assert rec.unavailable == []


# ---------------------------------------------------------------------------
# 4. Error math (pins from the .NET schema contract)
# ---------------------------------------------------------------------------

class TestErrorMath:
    def test_percentage_error_is_in_percent_units_and_signed(self):
        # +10 on an actual of 100 is +10 PERCENT, not 0.1.
        assert snapshots._percentage_error(10.0, 100.0) == pytest.approx(10.0)
        assert snapshots._percentage_error(-10.0, 100.0) == pytest.approx(-10.0)

    def test_percentage_error_uses_the_regression_metrics_clip(self):
        """Same 1e-6 denominator clip as train/evaluate.regression_metrics, so a
        percentage error computed here and there agree exactly."""
        import numpy as np
        from agriforecast_ml.train.evaluate import regression_metrics
        assert snapshots._percentage_error(1.0, 0.0) == pytest.approx(1.0 / 1e-6 * 100)
        # Cross-check against the trainer's own MAPE on the same numbers.
        mape = regression_metrics(np.array([50.0]), np.array([55.0]))["MAPE"]
        assert abs(snapshots._percentage_error(5.0, 50.0)) == pytest.approx(mape)

    def test_error_columns_written_by_the_mature_pass(self, monkeypatch):
        monkeypatch.setattr(predict, "_last_avgprice_at_or_before",
                            lambda cid, d, mbd=None: (90.0, date(2026, 3, 1)))
        rec = MatureRecorder()
        rec.install(monkeypatch, [_pending("r1", CROP_A, date(2026, 3, 1),
                                           p50=100.0, p10=80.0, p90=120.0)])
        snapshots._mature_pass(object(), date(2026, 3, 1), dry_run=False)
        f = rec.matured[0]
        assert f["actual_price"] == 90.0
        assert f["signed_error"] == pytest.approx(10.0)      # predicted - actual
        assert f["absolute_error"] == pytest.approx(10.0)
        assert f["percentage_error"] == pytest.approx(10.0 / 90.0 * 100)
        assert f["within_interval"] is True
        assert f["actual_observed_date"] == date(2026, 3, 1)

    @pytest.mark.parametrize("actual,expected", [
        (80.0, True),    # exactly the lower bound - inclusive
        (120.0, True),   # exactly the upper bound - inclusive
        (79.99, False),
        (120.01, False),
    ])
    def test_within_interval_is_inclusive_on_both_ends(self, monkeypatch, actual, expected):
        monkeypatch.setattr(predict, "_last_avgprice_at_or_before",
                            lambda cid, d, mbd=None: (actual, d))
        rec = MatureRecorder()
        rec.install(monkeypatch, [_pending("r1", CROP_A, date(2026, 3, 1),
                                           p50=100.0, p10=80.0, p90=120.0)])
        snapshots._mature_pass(object(), date(2026, 3, 1), dry_run=False)
        assert rec.matured[0]["within_interval"] is expected

    def test_max_harvest_date_matured_is_the_newest_scored_row(self, monkeypatch):
        monkeypatch.setattr(predict, "_last_avgprice_at_or_before",
                            lambda cid, d, mbd=None: (90.0, d))
        rec = MatureRecorder()
        rec.install(monkeypatch, [
            _pending("r1", CROP_A, date(2026, 2, 1)),
            _pending("r2", CROP_B, date(2026, 3, 5)),
            _pending("r3", CROP_A, date(2026, 2, 20)),
        ])
        summary, _ = snapshots._mature_pass(object(), date(2026, 3, 10), dry_run=False)
        assert summary["maxHarvestDateMatured"] == "2026-03-05"
        assert summary["matured"] == 3 and summary["stillPending"] == 0


# ---------------------------------------------------------------------------
# 5. Frozen predictions
# ---------------------------------------------------------------------------

_PREDICTION_COLUMNS = ["PredictedPrice", "LowerBound", "UpperBound", "Confidence",
                       "ActivePredictor", "ModelVersion", "FallbackTier", "ReasonCode",
                       "GrowthPeriodDays", "HarvestDate", "ReferencePrice"]


class TestFrozenPredictions:
    def test_mature_sql_touches_no_prediction_column(self):
        """Static proof of PRD 3.2: the maturing UPDATE's text may not so much as
        mention a prediction column."""
        sql = str(snapshots._MATURE_SQL)
        offenders = [c for c in _PREDICTION_COLUMNS if c in sql]
        assert not offenders, f"maturing UPDATE must never write {offenders}"

    def test_unavailable_sql_touches_no_prediction_column(self):
        sql = str(snapshots._UNAVAILABLE_SQL)
        offenders = [c for c in _PREDICTION_COLUMNS if c in sql]
        assert not offenders, f"give-up UPDATE must never write {offenders}"

    def test_mature_sql_only_transitions_out_of_pending(self):
        """Re-running the pass cannot re-mature a row: the WHERE re-checks the
        state, so a second UPDATE is a no-op rather than a rewrite."""
        sql = str(snapshots._MATURE_SQL)
        assert "MaturityState = 'pending'" in sql
        assert "MaturedAtUtc" in sql

    def test_unavailable_stamps_a_terminal_timestamp(self):
        """MaturedAtUtc means "reached a terminal state" - actual_unavailable is
        terminal, so it is stamped too."""
        sql = str(snapshots._UNAVAILABLE_SQL)
        assert "MaturedAtUtc" in sql
        assert "MaturityState = 'pending'" in sql

    def test_not_maturable_rows_carry_no_matured_timestamp(self, monkeypatch):
        """not_maturable is terminal at creation - CreatedAtUtc carries that
        instant, so the insert must not bind a MaturedAtUtc."""
        insert_sql = str(snapshots._UPSERT_SQL)
        head = insert_sql.split("WHEN NOT MATCHED")[1]
        assert "MaturedAtUtc" not in head

    def test_mature_pass_never_calls_the_snapshot_upsert(self, monkeypatch):
        """Behavioural sibling of the SQL proof: no path through maturing may
        reach the prediction writer."""
        def boom(*a, **k):
            raise AssertionError("maturing must never rewrite a prediction")
        monkeypatch.setattr(snapshots, "_upsert_snapshot", boom)
        monkeypatch.setattr(predict, "_last_avgprice_at_or_before",
                            lambda cid, d, mbd=None: (90.0, d))
        rec = MatureRecorder()
        rec.install(monkeypatch, [_pending("r1", CROP_A, date(2026, 3, 1))])
        summary, failures = snapshots._mature_pass(object(), date(2026, 3, 1), dry_run=False)
        assert summary["matured"] == 1 and failures == 0

    def test_upsert_leaves_a_matured_row_alone(self, monkeypatch):
        """A snapshot re-run over an already-matured day must not re-predict
        history. The fake store implements the MERGE's MATCHED predicate."""
        store = FakeSnapshotStore()
        key = (CROP_A, date(2026, 1, 1))
        store.rows[key] = {"crop_id": CROP_A, "snapshot_date": date(2026, 1, 1),
                           "predicted_price": 100.0,
                           "maturity_state": snapshots.MATURITY_MATURED}
        _install_snapshot_fakes(monkeypatch, store=store)
        summary, failures = snapshots._snapshot_pass(
            object(), date(2026, 1, 1), dry_run=False)
        assert store.actions == ["FROZEN"]
        assert store.rows[key]["predicted_price"] == 100.0   # untouched
        assert summary["inserted"] == 0 and summary["updated"] == 0
        assert failures == 0

    def test_merge_matched_predicate_excludes_terminal_states(self):
        """The frozen rule lives in the SQL, not only in the fake: MATCHED must
        be gated on a non-terminal state."""
        sql = str(snapshots._UPSERT_SQL)
        assert "WHEN MATCHED AND t.MaturityState IN ('pending', 'not_maturable')" in sql
        assert "matured" not in sql.split("WHEN MATCHED")[1].split("THEN")[0]


# ---------------------------------------------------------------------------
# 6. Snapshot pass - idempotency, counts, coverage
# ---------------------------------------------------------------------------

class TestSnapshotPass:
    def test_second_run_on_the_same_date_updates_and_never_duplicates(self, monkeypatch):
        crops = [{"cropId": CROP_A, "cropName": "A"}, {"cropId": CROP_B, "cropName": "B"}]
        store = _install_snapshot_fakes(monkeypatch, crops=crops)
        d = date(2026, 1, 1)

        first, _ = snapshots._snapshot_pass(object(), d, dry_run=False)
        assert (first["inserted"], first["updated"]) == (2, 0)

        second, _ = snapshots._snapshot_pass(object(), d, dry_run=False)
        assert (second["inserted"], second["updated"]) == (0, 2)

        # One row per crop per day - the unique key held.
        assert len(store.rows) == 2

    def test_a_different_date_inserts_a_new_row(self, monkeypatch):
        store = _install_snapshot_fakes(monkeypatch)
        snapshots._snapshot_pass(object(), date(2026, 1, 1), dry_run=False)
        snapshots._snapshot_pass(object(), date(2026, 1, 2), dry_run=False)
        assert len(store.rows) == 2

    def test_every_attempted_crop_lands_in_exactly_one_bucket(self, monkeypatch):
        """inserted + updated + frozen + failures == cropsAttempted. Without the
        `frozen` count a re-run over matured days would report all zeros and be
        indistinguishable from a pass that silently did nothing."""
        crops = [{"cropId": CROP_A, "cropName": "A"},
                 {"cropId": CROP_B, "cropName": "B"},
                 {"cropId": "cccccccc-0000-0000-0000-000000000003", "cropName": "C"},
                 {"cropId": "dddddddd-0000-0000-0000-000000000004", "cropName": "Bad"}]
        d = date(2026, 1, 1)
        store = FakeSnapshotStore()
        # A: already matured -> frozen.  B: pending -> updated.  C: absent -> inserted.
        store.rows[(CROP_A, d)] = {"maturity_state": snapshots.MATURITY_MATURED}
        store.rows[(CROP_B, d)] = {"maturity_state": snapshots.MATURITY_PENDING}

        def payload_for(cid, pd_):
            if cid.startswith("dddddddd"):
                raise RuntimeError("boom")
            return _payload(cid, plant_date=pd_)

        _install_snapshot_fakes(monkeypatch, crops=crops, payload_for=payload_for,
                                store=store)
        s, failures = snapshots._snapshot_pass(object(), d, dry_run=False)
        assert (s["inserted"], s["updated"], s["frozen"], failures) == (1, 1, 1, 1)
        assert s["inserted"] + s["updated"] + s["frozen"] + failures == s["cropsAttempted"]

    def test_a_non_positive_growth_period_is_not_maturable(self, monkeypatch):
        """A 0 or negative horizon would put the harvest date on or before the
        plant date - not a forecast at all."""
        store = _install_snapshot_fakes(
            monkeypatch,
            payload_for=lambda cid, d: _payload(cid, gp=0, plant_date=d))
        summary, _ = snapshots._snapshot_pass(object(), date(2026, 1, 1), dry_run=False)
        assert summary["notMaturable"] == 1
        row = store.rows[(CROP_A, date(2026, 1, 1))]
        assert row["harvest_date"] is None
        assert row["maturity_state"] == snapshots.MATURITY_NOT_MATURABLE

    def test_upsert_sql_casts_the_key_types_explicitly(self):
        """CropId is uniqueidentifier and SnapshotDate is date in the .NET schema;
        an implicit conversion on the join can also cost the unique index."""
        sql = str(snapshots._UPSERT_SQL)
        assert "CAST(:crop_id AS uniqueidentifier)" in sql
        assert "CAST(:snapshot_date AS date)" in sql
        # The INSERT reuses the already-converted source columns.
        assert "VALUES (:id, s.CropId, s.SnapshotDate" in sql

    def test_maturity_states_are_exact_lowercase(self):
        """The .NET CHECK constraint uses a binary collation: wrong casing is a
        hard INSERT failure, not a silently-stored variant."""
        assert snapshots.MATURITY_PENDING == "pending"
        assert snapshots.MATURITY_MATURED == "matured"
        assert snapshots.MATURITY_ACTUAL_UNAVAILABLE == "actual_unavailable"
        assert snapshots.MATURITY_NOT_MATURABLE == "not_maturable"

    def test_upsert_sql_keys_on_crop_and_snapshot_date_only(self):
        """ModelVersion is deliberately NOT part of the key: a day belongs to
        whichever version served it."""
        sql = str(snapshots._UPSERT_SQL)
        on_clause = sql.split("ON ")[1].split("WHEN")[0]
        assert "t.CropId = s.CropId" in on_clause
        assert "t.SnapshotDate = s.SnapshotDate" in on_clause
        assert "ModelVersion" not in on_clause

    def test_unresolvable_growth_period_still_gets_a_row(self, monkeypatch):
        """A crop with no verified agronomy profile is recorded not_maturable,
        never dropped - otherwise the pass's coverage would silently lie."""
        store = _install_snapshot_fakes(
            monkeypatch,
            payload_for=lambda cid, d: _payload(cid, gp=None, plant_date=d,
                                                active="crop_mean_fallback"))
        summary, failures = snapshots._snapshot_pass(
            object(), date(2026, 1, 1), dry_run=False)
        assert summary["notMaturable"] == 1
        assert summary["inserted"] == 1 and failures == 0
        row = store.rows[(CROP_A, date(2026, 1, 1))]
        assert row["maturity_state"] == snapshots.MATURITY_NOT_MATURABLE
        assert row["harvest_date"] is None and row["growth_period_days"] is None

    def test_harvest_date_comes_from_the_served_growth_period(self, monkeypatch):
        store = _install_snapshot_fakes(
            monkeypatch,
            payload_for=lambda cid, d: _payload(cid, gp=75, plant_date=d))
        snapshots._snapshot_pass(object(), date(2026, 1, 1), dry_run=False)
        row = store.rows[(CROP_A, date(2026, 1, 1))]
        assert row["growth_period_days"] == 75
        assert row["harvest_date"] == date(2026, 1, 1) + timedelta(days=75)
        assert row["maturity_state"] == snapshots.MATURITY_PENDING

    def test_model_and_fallback_are_counted_separately(self, monkeypatch):
        crops = [{"cropId": CROP_A, "cropName": "A"}, {"cropId": CROP_B, "cropName": "B"}]

        def payload_for(cid, d):
            active = "residual" if cid == CROP_A else "crop_mean_fallback"
            return _payload(cid, plant_date=d, active=active)

        _install_snapshot_fakes(monkeypatch, crops=crops, payload_for=payload_for)
        summary, _ = snapshots._snapshot_pass(object(), date(2026, 1, 1), dry_run=False)
        assert summary["modelServed"] == 1
        assert summary["fallbackServed"] == 1
        assert summary["cropsAttempted"] == 2

    def test_reference_price_is_the_plant_date_anchor(self, monkeypatch):
        """ReferencePrice must be the price known AT the plant date - the same
        carry-forward anchor serving used - so directional accuracy can never be
        computed against a post-plant number."""
        seen = []
        store = _install_snapshot_fakes(monkeypatch)
        monkeypatch.setattr(predict, "_carry_forward_price",
                            lambda cid, d: (seen.append((cid, d)), 27.5)[1])
        snapshots._snapshot_pass(object(), date(2026, 1, 1), dry_run=False)
        assert seen == [(CROP_A, date(2026, 1, 1))]
        assert store.rows[(CROP_A, date(2026, 1, 1))]["reference_price"] == 27.5

    def test_no_anchor_stores_null_not_zero(self, monkeypatch):
        """No knowable price is NULL, never 0 - a 0 reference would read as a
        real price and poison directional accuracy."""
        store = _install_snapshot_fakes(monkeypatch, reference=None)
        snapshots._snapshot_pass(object(), date(2026, 1, 1), dry_run=False)
        assert store.rows[(CROP_A, date(2026, 1, 1))]["reference_price"] is None


# ---------------------------------------------------------------------------
# 7. Per-crop failure isolation
# ---------------------------------------------------------------------------

class TestFailureIsolation:
    def test_one_crop_raising_does_not_stop_the_others(self, monkeypatch):
        crops = [{"cropId": CROP_A, "cropName": "A"},
                 {"cropId": "cccccccc-0000-0000-0000-000000000003", "cropName": "Bad"},
                 {"cropId": CROP_B, "cropName": "B"}]

        def payload_for(cid, d):
            if cid.startswith("cccccccc"):
                raise RuntimeError("this crop explodes")
            return _payload(cid, plant_date=d)

        store = _install_snapshot_fakes(monkeypatch, crops=crops, payload_for=payload_for)
        summary, failures = snapshots._snapshot_pass(
            object(), date(2026, 1, 1), dry_run=False)

        assert failures == 1
        assert summary["cropsAttempted"] == 3
        assert summary["inserted"] == 2
        assert set(k[0] for k in store.rows) == {CROP_A, CROP_B}

    def test_a_write_failure_is_also_isolated(self, monkeypatch):
        crops = [{"cropId": CROP_A, "cropName": "A"}, {"cropId": CROP_B, "cropName": "B"}]
        store = FakeSnapshotStore()

        def flaky_upsert(engine, row):
            if row["crop_id"] == CROP_A:
                raise RuntimeError("DB said no")
            return store.upsert(engine, row)

        _install_snapshot_fakes(monkeypatch, crops=crops, store=store)
        monkeypatch.setattr(snapshots, "_upsert_snapshot", flaky_upsert)
        summary, failures = snapshots._snapshot_pass(
            object(), date(2026, 1, 1), dry_run=False)
        assert failures == 1 and summary["inserted"] == 1

    def test_a_failing_mature_row_leaves_the_row_pending(self, monkeypatch):
        """A row we could not score is still pending, not silently resolved."""
        def flaky(cid, d, mbd=None):
            if cid == CROP_A:
                raise RuntimeError("read failed")
            return (90.0, d)

        monkeypatch.setattr(predict, "_last_avgprice_at_or_before", flaky)
        rec = MatureRecorder()
        rec.install(monkeypatch, [_pending("r1", CROP_A, date(2026, 3, 1)),
                                  _pending("r2", CROP_B, date(2026, 3, 1))])
        summary, failures = snapshots._mature_pass(object(), date(2026, 3, 1), dry_run=False)
        assert failures == 1
        assert summary["matured"] == 1
        assert summary["stillPending"] == 1     # the failed row is still pending
        assert rec.unavailable == []

    def test_run_reports_failures_instead_of_raising(self, monkeypatch):
        _install_snapshot_fakes(
            monkeypatch,
            payload_for=lambda cid, d: (_ for _ in ()).throw(RuntimeError("boom")))
        out = snapshots.run(date(2026, 1, 1), run_mature=False, today=date(2026, 1, 1))
        assert out["status"] == "ok"
        assert out["errors"]["snapshotCropFailures"] == 1


# ---------------------------------------------------------------------------
# 8. dryRun writes nothing
# ---------------------------------------------------------------------------

class TestDryRun:
    def test_dry_run_issues_no_snapshot_write(self, monkeypatch):
        def boom(*a, **k):
            raise AssertionError("dryRun must not write")
        _install_snapshot_fakes(monkeypatch)
        monkeypatch.setattr(snapshots, "_upsert_snapshot", boom)
        summary, failures = snapshots._snapshot_pass(
            object(), date(2026, 1, 1), dry_run=True)
        assert failures == 0
        assert summary["cropsAttempted"] == 1
        assert (summary["inserted"], summary["updated"]) == (0, 0)
        # It still reports what it WOULD have done.
        assert summary["modelServed"] == 1

    def test_dry_run_issues_no_maturity_write(self, monkeypatch):
        monkeypatch.setattr(predict, "_last_avgprice_at_or_before",
                            lambda cid, d, mbd=None: (90.0, d))
        rec = MatureRecorder()
        rec.install(monkeypatch, [_pending("r1", CROP_A, date(2026, 3, 1))])
        monkeypatch.setattr(snapshots, "_write_maturity",
                            lambda *a, **k: (_ for _ in ()).throw(
                                AssertionError("dryRun must not write")))
        summary, failures = snapshots._mature_pass(object(), date(2026, 3, 1), dry_run=True)
        assert failures == 0 and summary["matured"] == 1

    def test_dry_run_issues_no_unavailable_write(self, monkeypatch):
        monkeypatch.setattr(predict, "_last_avgprice_at_or_before",
                            lambda cid, d, mbd=None: None)
        rec = MatureRecorder()
        rec.install(monkeypatch, [_pending("r1", CROP_A, date(2026, 3, 1))])
        monkeypatch.setattr(snapshots, "_mark_unavailable",
                            lambda *a, **k: (_ for _ in ()).throw(
                                AssertionError("dryRun must not write")))
        summary, failures = snapshots._mature_pass(
            object(), date(2026, 4, 1), dry_run=True)
        assert failures == 0 and summary["markedUnavailable"] == 1


# ---------------------------------------------------------------------------
# 9. run() summary contract
# ---------------------------------------------------------------------------

class TestRunSummaryShape:
    SNAP = date(2026, 3, 1)
    TODAY = date(2026, 3, 2)

    def _run(self, monkeypatch, **kw):
        _install_snapshot_fakes(monkeypatch)
        monkeypatch.setattr(predict, "_last_avgprice_at_or_before",
                            lambda cid, d, mbd=None: (90.0, d))
        monkeypatch.setattr(snapshots, "_pending_rows",
                            lambda engine, today: [_pending("r1", CROP_A, date(2026, 3, 1))])
        monkeypatch.setattr(snapshots, "_write_maturity", lambda engine, fields: None)
        return snapshots.run(self.SNAP, today=self.TODAY, **kw)

    def test_exact_summary_keys(self, monkeypatch):
        out = self._run(monkeypatch)
        assert set(out) == {"status", "snapshot", "mature", "errors"}
        # PRD 4.2 keys plus the deliberate `frozen` addition.
        assert set(out["snapshot"]) == {"snapshotDate", "cropsAttempted", "inserted",
                                        "updated", "frozen", "modelServed",
                                        "fallbackServed", "notMaturable", "modelVersion"}
        assert set(out["mature"]) == {"scanned", "matured", "stillPending",
                                      "markedUnavailable", "maxHarvestDateMatured"}
        assert set(out["errors"]) == {"snapshotCropFailures", "matureRowFailures"}
        assert out["status"] == "ok"
        assert out["snapshot"]["snapshotDate"] == "2026-03-01"
        assert out["snapshot"]["modelVersion"] == "v17"
        assert out["mature"]["matured"] == 1

    def test_run_mature_false_keeps_the_shape_with_honest_zeros(self, monkeypatch):
        out = self._run(monkeypatch, run_mature=False)
        assert out["mature"] == {"scanned": 0, "matured": 0, "stillPending": 0,
                                 "markedUnavailable": 0, "maxHarvestDateMatured": None}

    def test_snapshot_date_accepts_an_iso_string(self, monkeypatch):
        _install_snapshot_fakes(monkeypatch)
        out = snapshots.run("2026-02-03", run_mature=False, today=date(2026, 2, 3))
        assert out["snapshot"]["snapshotDate"] == "2026-02-03"

    def test_snapshot_date_defaults_to_today(self, monkeypatch):
        _install_snapshot_fakes(monkeypatch)
        out = snapshots.run(run_mature=False)
        assert out["snapshot"]["snapshotDate"] == date.today().isoformat()


# ---------------------------------------------------------------------------
# 9b. snapshotDate bounds (B2)
# ---------------------------------------------------------------------------

class TestSnapshotDateBounds:
    TODAY = date(2026, 3, 10)

    def _run(self, monkeypatch, snap):
        _install_snapshot_fakes(monkeypatch)
        return snapshots.run(snap, run_mature=False, today=self.TODAY)

    def test_today_is_accepted(self, monkeypatch):
        out = self._run(monkeypatch, self.TODAY)
        assert out["snapshot"]["snapshotDate"] == self.TODAY.isoformat()

    def test_tomorrow_is_rejected(self, monkeypatch):
        """A forecast cannot be recorded as having been made on a day that has
        not happened - the row's whole value is that it is point-in-time."""
        with pytest.raises(snapshots.SnapshotDateError) as e:
            self._run(monkeypatch, self.TODAY + timedelta(days=1))
        assert "future" in str(e.value)

    def test_the_catch_up_bound_is_accepted_at_its_edge(self, monkeypatch):
        """Exactly SNAPSHOT_MATURE_GRACE_DAYS back is a legitimate catch-up."""
        snap = self.TODAY - timedelta(days=snapshots.SNAPSHOT_MATURE_GRACE_DAYS)
        out = self._run(monkeypatch, snap)
        assert out["snapshot"]["snapshotDate"] == snap.isoformat()

    def test_one_day_past_the_catch_up_bound_is_rejected(self, monkeypatch):
        """Re-predicting further back would store a hindsight forecast as if it
        had been made at the time. Backfill needs its own labelled path."""
        snap = self.TODAY - timedelta(days=snapshots.SNAPSHOT_MATURE_GRACE_DAYS + 1)
        with pytest.raises(snapshots.SnapshotDateError) as e:
            self._run(monkeypatch, snap)
        assert "hindsight" in str(e.value)

    def test_rejection_happens_before_any_work(self, monkeypatch):
        """The refusal must not have already predicted or written anything."""
        def boom(*a, **k):
            raise AssertionError("nothing may run for a rejected date")
        _install_snapshot_fakes(monkeypatch)
        monkeypatch.setattr(snapshots, "_active_crops", boom)
        with pytest.raises(snapshots.SnapshotDateError):
            snapshots.run(self.TODAY + timedelta(days=1), today=self.TODAY)

    def test_a_rejected_date_is_not_a_pass_failure(self):
        """SnapshotDateError must stay distinguishable from a pass failure, or
        the API could not tell 422 from 502."""
        assert issubclass(snapshots.SnapshotDateError, ValueError)


# ---------------------------------------------------------------------------
# 10. Static leakage guards (PRD 3.1)
# ---------------------------------------------------------------------------

# The write-only serving artifacts, by name.
_FORBIDDEN_TABLES = (
    "ForecastSnapshots",
    "UserSales",
    "UserCropWatchlist",
    # The per-crop watched markets. A substring scan for "UserCropWatchlist" does NOT cover this
    # table - the name is "UserCropWatchMarkets", not a suffix of the parent - so it is listed
    # explicitly. Which markets a farmer follows is a display choice and must never become a feature.
    "UserCropWatchMarkets",
)

# Reaching the same tables through the writer's own API. A loader could import this
# module and call its primitives without ever typing "ForecastSnapshots", and a
# table-name scan alone would wave that straight through - so the import path is banned
# on the training side too.
_FORBIDDEN_MODULE_REFS = ("serving.snapshots", "import snapshots", "snapshots.TABLE")

# The single sanctioned writer. Everything else on the training/feature side must
# not even name the table.
_SNAPSHOT_WRITER = ML_ROOT / "agriforecast_ml" / "serving" / "snapshots.py"

# ...and the single sanctioned CALLER of it: the HTTP entry point. Any third module
# importing the writer is the leak this guard exists to catch.
_SNAPSHOT_CALLER = ML_ROOT / "agriforecast_ml" / "serving" / "app.py"


def _all_sources() -> list[Path]:
    """Every Python module that can reach the model's inputs: the whole package
    plus the top-level pipeline scripts.

    Scanning the WHOLE tree (rather than a hand-listed set of loaders) is
    deliberate - a future loader added anywhere is covered without anyone
    remembering to extend this list.
    """
    return sorted((ML_ROOT / "agriforecast_ml").rglob("*.py")) + sorted(ML_ROOT.glob("*.py"))


def _scan(tokens, allowed: set[Path]) -> list[str]:
    allowed = {p.resolve() for p in allowed}
    offenders = []
    for path in _all_sources():
        if path.resolve() in allowed:
            continue
        src = path.read_text(encoding="utf-8")
        offenders += [f"{path.relative_to(ML_ROOT)}: {t}" for t in tokens if t in src]
    return offenders


class TestLeakageGuards:
    def test_no_other_module_names_the_serving_artifacts(self):
        offenders = _scan(_FORBIDDEN_TABLES, {_SNAPSHOT_WRITER})
        assert not offenders, (
            "Forecast snapshots and user-entered sales are write-only serving "
            "artifacts. A module on the feature/training path referencing them is "
            "a self-referential lookahead: " + "; ".join(offenders))

    def test_no_other_module_imports_the_snapshot_writer(self):
        """The bypass the table-name scan cannot see: import the writer, call its
        primitives, never type the table name."""
        offenders = _scan(_FORBIDDEN_MODULE_REFS, {_SNAPSHOT_WRITER, _SNAPSHOT_CALLER})
        assert not offenders, (
            "Only the serving app may import the snapshot writer; reaching it from "
            "anywhere else routes serving output back toward the model: "
            + "; ".join(offenders))

    def test_the_loaders_are_specifically_clean(self):
        """Named-and-shamed subset, so a failure points straight at the law."""
        for rel in ("agriforecast_ml/load.py", "agriforecast_ml/features.py",
                    "agriforecast_ml/train/dataset.py", "agriforecast_ml/store.py",
                    "build_features.py"):
            src = (ML_ROOT / rel).read_text(encoding="utf-8")
            for name in _FORBIDDEN_TABLES + _FORBIDDEN_MODULE_REFS:
                assert name not in src, f"{rel} must never read {name}"

    def test_the_module_ref_tokens_would_actually_fire(self, tmp_path):
        """Non-vacuity for the bypass guard: a synthetic loader that imports the
        writer without naming the table must be caught by SOME token."""
        leak = ("from agriforecast_ml.serving import snapshots\n"
                "rows = snapshots._pending_rows(engine, today)\n")
        assert any(t in leak for t in _FORBIDDEN_MODULE_REFS)
        # ...and the table-name scan alone would NOT have caught it.
        assert not any(t in leak for t in _FORBIDDEN_TABLES)

    def test_the_guard_is_not_vacuous(self):
        """If the writer itself stopped naming the table, the scan above would
        pass for the wrong reason."""
        src = _SNAPSHOT_WRITER.read_text(encoding="utf-8")
        assert "ForecastSnapshots" in src
        assert snapshots.TABLE == "ForecastSnapshots"

    def test_snapshots_module_never_writes_the_feature_store(self):
        """The arrow points one way: snapshots READS CropFeatureDaily (through the
        shared helper) and must never write to it or to PriceObservations."""
        src = _SNAPSHOT_WRITER.read_text(encoding="utf-8")
        for banned in ("INSERT INTO CropFeatureDaily", "UPDATE CropFeatureDaily",
                       "INSERT INTO PriceObservations", "UPDATE PriceObservations",
                       "to_sql"):
            assert banned not in src


# ---------------------------------------------------------------------------
# 11. _upsert_snapshot action decoding (the one primitive with real SQL)
# ---------------------------------------------------------------------------

class _FakeResult:
    def __init__(self, rows):
        self._rows = rows

    def fetchall(self):
        return self._rows


class _FakeConn:
    def __init__(self, rows):
        self._rows = rows
        self.executed = []

    def execute(self, sql, params=None):
        self.executed.append((str(sql), params))
        return _FakeResult(self._rows)

    def __enter__(self):
        return self

    def __exit__(self, *a):
        return False


class _FakeEngine:
    def __init__(self, rows):
        self.conn = _FakeConn(rows)

    def begin(self):
        return self.conn


class TestUpsertActionDecoding:
    def _row(self):
        return {"crop_id": CROP_A, "snapshot_date": date(2026, 1, 1),
                "harvest_date": date(2026, 4, 1), "growth_period_days": 90,
                "predicted_price": 100.0, "lower_bound": 80.0, "upper_bound": 120.0,
                "reference_price": 27.5, "confidence": "High",
                "active_predictor": "model", "fallback_tier": "crop",
                "model_version": "v17", "reason_code": "model_served",
                "maturity_state": snapshots.MATURITY_PENDING}

    @pytest.mark.parametrize("rows,expected", [
        ([("INSERT",)], "INSERT"),
        ([("UPDATE",)], "UPDATE"),
        ([], "FROZEN"),          # MATCHED but terminal -> MERGE emitted no action
    ])
    def test_action_is_read_from_the_merge_output(self, rows, expected):
        engine = _FakeEngine(rows)
        assert snapshots._upsert_snapshot(engine, self._row()) == expected

    def test_upsert_stamps_an_id_and_a_naive_utc_created_at(self):
        engine = _FakeEngine([("INSERT",)])
        snapshots._upsert_snapshot(engine, self._row())
        _, params = engine.conn.executed[0]
        assert params["id"] and params["created_at_utc"].tzinfo is None
        # The caller's row is not mutated by the write.
        assert "id" not in self._row()


# ---------------------------------------------------------------------------
# 12. Endpoint wiring
# ---------------------------------------------------------------------------

SNAPSHOT_PATH = "/admin/snapshot-forecasts"


@pytest.fixture(scope="module")
def app(serving_app_with_stubbed_predict):
    return serving_app_with_stubbed_predict


class TestSnapshotEndpoint:
    def test_route_requires_an_api_key(self, monkeypatch, app):
        from starlette.testclient import TestClient
        monkeypatch.setenv("ML_ADMIN_API_KEY", "k")
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(SNAPSHOT_PATH, json={})
        assert resp.status_code == 401
        assert resp.status_code != 404, "route must be registered on admin_router"

    def test_returns_the_summary_verbatim(self, monkeypatch, app):
        from starlette.testclient import TestClient
        monkeypatch.setenv("ML_ADMIN_API_KEY", "k")
        summary = {"status": "ok",
                   "snapshot": {"snapshotDate": "2026-01-01", "cropsAttempted": 1,
                                "inserted": 1, "updated": 0, "frozen": 0,
                                "modelServed": 1, "fallbackServed": 0,
                                "notMaturable": 0, "modelVersion": "v17"},
                   "mature": {"scanned": 0, "matured": 0, "stillPending": 0,
                              "markedUnavailable": 0, "maxHarvestDateMatured": None},
                   "errors": {"snapshotCropFailures": 0, "matureRowFailures": 0}}
        seen = {}

        def fake_run(snapshot_date=None, *, run_mature=True, dry_run=False, today=None):
            seen.update(snapshot_date=snapshot_date, run_mature=run_mature,
                        dry_run=dry_run)
            return summary

        monkeypatch.setattr(snapshots, "run", fake_run)
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(SNAPSHOT_PATH, headers={"X-API-Key": "k"},
                           json={"snapshotDate": "2026-01-01", "dryRun": True})
        assert resp.status_code == 200
        assert resp.json() == summary
        assert seen["snapshot_date"] == date(2026, 1, 1)
        assert seen["dry_run"] is True
        assert seen["run_mature"] is True     # defaults to on

    def test_a_rejected_snapshot_date_is_422_not_502(self, monkeypatch, app):
        """A refused request is the caller's problem to fix, not a server fault -
        the Worker must be able to tell "I asked wrongly" from "the pass broke"."""
        from starlette.testclient import TestClient
        monkeypatch.setenv("ML_ADMIN_API_KEY", "k")
        monkeypatch.setattr(
            snapshots, "run",
            lambda *a, **k: (_ for _ in ()).throw(
                snapshots.SnapshotDateError("snapshotDate 2030-01-01 is in the future")))
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(SNAPSHOT_PATH, headers={"X-API-Key": "k"},
                           json={"snapshotDate": "2030-01-01"})
        assert resp.status_code == 422
        assert "future" in resp.json()["detail"]
        assert "Traceback" not in resp.text

    def test_a_whole_pass_failure_is_a_502_not_a_traceback(self, monkeypatch, app):
        from starlette.testclient import TestClient
        monkeypatch.setenv("ML_ADMIN_API_KEY", "k")
        monkeypatch.setattr(snapshots, "run",
                            lambda *a, **k: (_ for _ in ()).throw(RuntimeError("boom")))
        client = TestClient(app, raise_server_exceptions=False)
        resp = client.post(SNAPSHOT_PATH, headers={"X-API-Key": "k"}, json={})
        assert resp.status_code == 502
        assert "boom" not in resp.text and "Traceback" not in resp.text


# ---------------------------------------------------------------------------
# 13. Live schema conformance (skips without a DB / before the migration)
# ---------------------------------------------------------------------------

class TestLiveSchemaConformance:
    def test_every_bound_column_exists_in_the_table(self):
        """Read-only check that the columns this module writes are the columns
        the .NET migration created. Skips when there is no DB or the table has
        not been migrated yet."""
        import os
        if not os.environ.get("AGRI_DB_HOST"):
            pytest.skip("no AGRI_DB_* in environment")
        import sqlalchemy as sa
        from agriforecast_ml.db import get_engine
        try:
            with get_engine().connect() as conn:
                cols = {r[0] for r in conn.execute(sa.text(
                    "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS "
                    "WHERE TABLE_NAME = 'ForecastSnapshots'")).fetchall()}
        except Exception as e:  # noqa: BLE001 - live-DB availability guard
            pytest.skip(f"DB unreachable: {e}")
        if not cols:
            pytest.skip("ForecastSnapshots not migrated yet (.NET PR 0a)")
        expected = {"Id", "CropId", "SnapshotDate", "HarvestDate", "GrowthPeriodDays",
                    "PredictedPrice", "LowerBound", "UpperBound", "ReferencePrice",
                    "Confidence", "ActivePredictor", "FallbackTier", "ModelVersion",
                    "ReasonCode", "MaturityState", "ActualPrice", "ActualObservedDate",
                    "SignedError", "AbsoluteError", "PercentageError", "WithinInterval",
                    "CreatedAtUtc", "MaturedAtUtc"}
        assert expected <= cols, f"missing columns: {sorted(expected - cols)}"
