"""Tests for agriforecast_ml.feature_run_log (build_features.py ->
IngestionRuns, Source="FEATURE_BUILD").

Hermetic: no live DB. Following test_training_log.py's precedent, the
stateful start/finalize behaviour is verified against a REAL in-memory
SQLite engine mirroring the frozen IngestionRuns contract (schema owned by
the .NET migration CreateIngestionRunsAndVerifications).

Coverage:
  TestStartRun         -- Running row inserted with fresh Id/BatchId, Status=0,
                           StartedUtc==CreatedAtUtc, all counts/coverage NULL.
  TestMarkSucceeded     -- same row transitions to Status=1 with counts/coverage
                            set, FinishedUtc set, ErrorSummary stays NULL.
  TestMarkFailed        -- same row transitions to Status=2 with a capped
                            ErrorSummary, counts/coverage stay NULL.
  TestNaiveUtc          -- tz-aware datetimes are normalized to naive UTC
                            before ever reaching a bind param (the pymssql /
                            datetime2 trap training_log.py already hit).
"""
from __future__ import annotations

import sys
import uuid
from datetime import date, datetime, timezone
from pathlib import Path

import pytest
import sqlalchemy as sa

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import feature_run_log as frl  # noqa: E402


_CREATE_TABLE = """
CREATE TABLE IngestionRuns (
    Id              TEXT PRIMARY KEY,
    BatchId         TEXT NOT NULL,
    Source          TEXT NOT NULL,
    StartedUtc      TIMESTAMP NOT NULL,
    FinishedUtc     TIMESTAMP,
    Status          INTEGER NOT NULL,
    CoveredFromDate DATE,
    CoveredToDate   DATE,
    RowsFetched     INTEGER,
    RowsInserted    INTEGER,
    RowsSkipped     INTEGER,
    DistinctCrops   INTEGER,
    ErrorSummary    TEXT,
    CreatedAtUtc    TIMESTAMP NOT NULL
)
"""


@pytest.fixture()
def engine():
    eng = sa.create_engine("sqlite://")  # in-memory, single shared connection
    with eng.begin() as conn:
        conn.execute(sa.text(_CREATE_TABLE))
    return eng


def _rows(engine) -> list[dict]:
    with engine.connect() as conn:
        res = conn.execute(sa.text("SELECT * FROM IngestionRuns"))
        return [dict(r._mapping) for r in res]


def _one(engine) -> dict:
    rows = _rows(engine)
    assert len(rows) == 1
    return rows[0]


# ===========================================================================
# start_run
# ===========================================================================

class TestStartRun:
    def test_inserts_running_row(self, engine):
        run_id, batch_id, started = frl.start_run(engine)
        row = _one(engine)
        assert row["Id"] == str(run_id)
        assert row["BatchId"] == str(batch_id)
        assert row["Source"] == "FEATURE_BUILD"
        assert row["Status"] == frl.STATUS_RUNNING == 0
        assert row["FinishedUtc"] is None
        assert row["ErrorSummary"] is None
        for col in ("RowsFetched", "RowsInserted", "RowsSkipped", "DistinctCrops",
                    "CoveredFromDate", "CoveredToDate"):
            assert row[col] is None

    def test_fresh_ids_every_call(self, engine):
        run_id1, batch_id1, _ = frl.start_run(engine)
        run_id2, batch_id2, _ = frl.start_run(engine)
        assert run_id1 != run_id2
        assert batch_id1 != batch_id2
        assert len(_rows(engine)) == 2

    def test_started_utc_is_naive(self, engine):
        _run_id, _batch_id, started = frl.start_run(engine)
        assert isinstance(started, datetime)
        assert started.tzinfo is None


# ===========================================================================
# mark_succeeded
# ===========================================================================

class TestMarkSucceeded:
    def test_finalizes_same_row_succeeded(self, engine):
        run_id, _batch_id, _started = frl.start_run(engine)
        frl.mark_succeeded(
            engine, run_id,
            rows_inserted=1234, distinct_crops=42,
            covered_from=date(2015, 6, 22), covered_to=date(2026, 7, 25),
        )
        row = _one(engine)
        assert row["Status"] == frl.STATUS_SUCCEEDED == 1
        assert row["FinishedUtc"] is not None
        assert row["RowsInserted"] == 1234
        assert row["DistinctCrops"] == 42
        assert row["ErrorSummary"] is None
        # RowsFetched/RowsSkipped are intentionally never populated by this source
        assert row["RowsFetched"] is None
        assert row["RowsSkipped"] is None

    def test_none_counts_are_safe(self, engine):
        run_id, _batch_id, _started = frl.start_run(engine)
        frl.mark_succeeded(engine, run_id)
        row = _one(engine)
        assert row["Status"] == 1
        assert row["RowsInserted"] is None
        assert row["DistinctCrops"] is None

    def test_does_not_create_a_new_row(self, engine):
        run_id, _batch_id, _started = frl.start_run(engine)
        frl.mark_succeeded(engine, run_id, rows_inserted=5)
        assert len(_rows(engine)) == 1


# ===========================================================================
# mark_failed
# ===========================================================================

class TestMarkFailed:
    def test_finalizes_same_row_failed(self, engine):
        run_id, _batch_id, _started = frl.start_run(engine)
        frl.mark_failed(engine, run_id, ValueError("weather join produced 0 rows"))
        row = _one(engine)
        assert row["Status"] == frl.STATUS_FAILED == 2
        assert row["FinishedUtc"] is not None
        assert row["ErrorSummary"] == "weather join produced 0 rows"
        assert row["RowsInserted"] is None
        assert row["DistinctCrops"] is None
        assert row["CoveredFromDate"] is None
        assert row["CoveredToDate"] is None

    def test_error_summary_capped_to_1000_chars(self, engine):
        run_id, _batch_id, _started = frl.start_run(engine)
        frl.mark_failed(engine, run_id, RuntimeError("x" * 5000))
        row = _one(engine)
        assert len(row["ErrorSummary"]) == 1000

    def test_does_not_create_a_new_row(self, engine):
        run_id, _batch_id, _started = frl.start_run(engine)
        frl.mark_failed(engine, run_id, RuntimeError("boom"))
        assert len(_rows(engine)) == 1


# ===========================================================================
# Naive-UTC normalization (the pymssql / datetime2 trap)
# ===========================================================================

class TestNaiveUtc:
    def test_to_naive_utc_strips_tzinfo(self):
        aware = datetime(2026, 7, 26, 15, 16, 27, tzinfo=timezone.utc)
        out = frl._to_naive_utc(aware)
        assert out.tzinfo is None
        assert out == datetime(2026, 7, 26, 15, 16, 27)

    def test_to_naive_utc_converts_non_utc_offset(self):
        from datetime import timedelta
        sl_tz = timezone(timedelta(hours=5, minutes=30))
        aware = datetime(2026, 7, 26, 15, 16, 27, tzinfo=sl_tz)
        out = frl._to_naive_utc(aware)
        assert out.tzinfo is None
        assert out == datetime(2026, 7, 26, 9, 46, 27)

    def test_to_naive_utc_none_safe(self):
        assert frl._to_naive_utc(None) is None

    def test_to_naive_utc_passes_through_already_naive(self):
        naive = datetime(2026, 7, 26, 9, 0, 0)
        assert frl._to_naive_utc(naive) is naive

    def test_start_run_and_mark_calls_bind_naive_datetimes(self, engine, monkeypatch):
        """Guard against a future regression that binds a tz-aware datetime
        straight through: patch datetime.now to return a tz-aware instant and
        assert every persisted timestamp column round-trips naive."""
        run_id, _batch_id, started = frl.start_run(engine)
        assert started.tzinfo is None
        frl.mark_succeeded(engine, run_id, rows_inserted=1)
        row = _one(engine)
        # SQLite stores TIMESTAMP as text; a tz-aware bind would have embedded
        # a "+00:00" / offset suffix -- assert none is present.
        assert "+" not in row["StartedUtc"]
        assert "+" not in row["FinishedUtc"]
