"""Tests for trigger_forecast_snapshot.py: the nightly HTTP caller of ml-serving's
POST /admin/snapshot-forecasts, plus its ONE IngestionRuns row per attempt
(Source="FORECAST_SNAPSHOT").

Hermetic: no live DB, no live network. `run()`/`skip()` take an injected SQLite
engine (mirrors test_feature_run_log.py's/test_snapshot_run_log.py's in-memory
IngestionRuns table) and an injected `http_post` callable standing in for the real
urllib call, so every branch of the decision table is driven directly:

  ok, 0 failures                -> Succeeded, counts recorded
  ok, snapshotCropFailures>0    -> Failed (THE 200-"ok"-with-failures trap)
  ok, matureRowFailures>0       -> Failed
  transport error (raises)      -> Failed, distinct "Transport failure" text
  HTTP 422                      -> Failed, distinct "CALLER-BUG" text
  HTTP 500 / non-200            -> Failed, body included
  unparsable body                -> Failed (payload is None)
  disabled flag                 -> Skipped, NO http_post call made
  never raises                  -> every branch above returns normally

_is_enabled() is tested directly for its truthy/falsy vocabulary.
"""
from __future__ import annotations

import sys
from datetime import datetime, timezone
from pathlib import Path

import pytest
import sqlalchemy as sa

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

import trigger_forecast_snapshot as trig  # noqa: E402
from agriforecast_ml import snapshot_run_log as srl  # noqa: E402


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
    eng = sa.create_engine("sqlite://")
    with eng.begin() as conn:
        conn.execute(sa.text(_CREATE_TABLE))
    return eng


def _rows(engine) -> list[dict]:
    with engine.connect() as conn:
        res = conn.execute(sa.text("SELECT * FROM IngestionRuns"))
        return [dict(r._mapping) for r in res]


def _one(engine) -> dict:
    rows = _rows(engine)
    assert len(rows) == 1, f"expected exactly one IngestionRuns row, got {len(rows)}: {rows}"
    return rows[0]


def _clean_payload(**overrides) -> dict:
    payload = {
        "status": "ok",
        "snapshot": {
            "snapshotDate": "2026-07-27", "cropsAttempted": 96, "inserted": 90,
            "updated": 2, "frozen": 4, "modelServed": 11, "fallbackServed": 85,
            "notMaturable": 4, "modelVersion": "v17",
        },
        "mature": {
            "scanned": 412, "matured": 47, "stillPending": 360,
            "markedUnavailable": 5, "maxHarvestDateMatured": "2026-07-20",
        },
        "errors": {"snapshotCropFailures": 0, "matureRowFailures": 0},
    }
    payload.update(overrides)
    return payload


def _http_post_returning(status: int, payload, raw: "str | None" = None):
    def _fn(base_url: str, api_key: str):
        return status, payload, raw if raw is not None else str(payload)
    return _fn


def _http_post_raising(exc: Exception):
    def _fn(base_url: str, api_key: str):
        raise exc
    return _fn


# --- the happy path -----------------------------------------------------------------

class TestCleanPass:
    def test_ok_zero_failures_marks_succeeded_with_counts(self, engine):
        outcome = trig.run(
            engine, "http://ml-serving:8077", "secret",
            http_post=_http_post_returning(200, _clean_payload()))

        assert outcome == "succeeded"
        row = _one(engine)
        assert row["Status"] == srl.STATUS_SUCCEEDED
        assert row["ErrorSummary"] is None
        assert row["RowsInserted"] == 92  # inserted(90) + updated(2)
        assert row["RowsSkipped"] == 4  # frozen
        assert row["RowsFetched"] == 412  # mature.scanned
        assert row["DistinctCrops"] == 96  # cropsAttempted
        assert str(row["CoveredFromDate"]) == "2026-07-27"
        assert str(row["CoveredToDate"]) == "2026-07-27"

    def test_only_one_row_written(self, engine):
        trig.run(engine, "http://x", "k", http_post=_http_post_returning(200, _clean_payload()))
        assert len(_rows(engine)) == 1


# --- the 200-"ok"-with-failures trap ------------------------------------------------

class TestOkButFailures:
    def test_snapshot_crop_failures_marks_failed_not_succeeded(self, engine):
        payload = _clean_payload(errors={"snapshotCropFailures": 3, "matureRowFailures": 0})

        outcome = trig.run(engine, "http://x", "k", http_post=_http_post_returning(200, payload))

        assert outcome == "failed"
        row = _one(engine)
        assert row["Status"] == srl.STATUS_FAILED
        assert row["ErrorSummary"] is not None
        assert "snapshotCropFailures=3" in row["ErrorSummary"]
        # the frozen count must be visible in the summary too -- PRD 4.2's snapshot.frozen
        assert "frozen=4" in row["ErrorSummary"]

    def test_mature_row_failures_marks_failed(self, engine):
        payload = _clean_payload(errors={"snapshotCropFailures": 0, "matureRowFailures": 2})

        outcome = trig.run(engine, "http://x", "k", http_post=_http_post_returning(200, payload))

        assert outcome == "failed"
        row = _one(engine)
        assert row["Status"] == srl.STATUS_FAILED
        assert "matureRowFailures=2" in row["ErrorSummary"]

    def test_non_ok_status_string_marks_failed_even_with_zero_failure_counts(self, engine):
        payload = _clean_payload(status="degraded")

        outcome = trig.run(engine, "http://x", "k", http_post=_http_post_returning(200, payload))

        assert outcome == "failed"
        assert _one(engine)["Status"] == srl.STATUS_FAILED


# --- transport / HTTP-status failures ------------------------------------------------

class TestTransportAndHttpFailures:
    def test_transport_error_marks_failed_distinctly(self, engine):
        outcome = trig.run(
            engine, "http://ml-serving:8077", "secret",
            http_post=_http_post_raising(ConnectionRefusedError("connection refused")))

        assert outcome == "failed"
        row = _one(engine)
        assert row["Status"] == srl.STATUS_FAILED
        assert "Transport failure" in row["ErrorSummary"]
        assert "ConnectionRefusedError" in row["ErrorSummary"]

    def test_http_422_marks_failed_distinctly_as_a_caller_bug(self, engine):
        outcome = trig.run(
            engine, "http://x", "k",
            http_post=_http_post_returning(422, {"detail": "snapshotDate is in the future"}))

        assert outcome == "failed"
        row = _one(engine)
        assert row["Status"] == srl.STATUS_FAILED
        assert "422" in row["ErrorSummary"]
        assert "CALLER-BUG" in row["ErrorSummary"]
        assert "not a pass failure" in row["ErrorSummary"]

    def test_http_500_marks_failed_with_body(self, engine):
        outcome = trig.run(
            engine, "http://x", "k",
            http_post=_http_post_returning(500, None, raw="Internal Server Error"))

        assert outcome == "failed"
        row = _one(engine)
        assert row["Status"] == srl.STATUS_FAILED
        assert "500" in row["ErrorSummary"]
        assert "Internal Server Error" in row["ErrorSummary"]

    def test_unparsable_200_body_marks_failed(self, engine):
        outcome = trig.run(
            engine, "http://x", "k",
            http_post=_http_post_returning(200, None, raw="not json"))

        assert outcome == "failed"
        assert _one(engine)["Status"] == srl.STATUS_FAILED


# --- disabled flag path ---------------------------------------------------------------

class TestSkip:
    def test_skip_marks_skipped_and_makes_no_http_call(self, engine):
        calls = []

        def _should_not_be_called(base_url, api_key):
            calls.append((base_url, api_key))
            return 200, _clean_payload(), "{}"

        outcome = trig.skip(engine)

        assert outcome == "skipped"
        assert calls == []
        row = _one(engine)
        assert row["Status"] == srl.STATUS_SKIPPED
        assert row["ErrorSummary"] is None


class TestIsEnabled:
    @pytest.mark.parametrize("raw,expected", [
        (None, True),           # unset -> enabled (default true)
        ("true", True),
        ("True", True),
        ("TRUE", True),
        ("1", True),
        ("yes", True),
        ("false", False),
        ("False", False),
        ("0", False),
        ("no", False),
        ("off", False),
        ("", False),
        ("  false  ", False),
    ])
    def test_enabled_vocabulary(self, monkeypatch, raw, expected):
        if raw is None:
            monkeypatch.delenv(trig._ENABLED_ENV, raising=False)
        else:
            monkeypatch.setenv(trig._ENABLED_ENV, raw)
        assert trig._is_enabled() is expected


# --- main() never raises, always returns 0 --------------------------------------------

class TestMainFailSoft:
    def test_disabled_path_returns_0_and_makes_no_http_call(self, monkeypatch, engine):
        monkeypatch.setenv(trig._ENABLED_ENV, "false")
        monkeypatch.setattr(trig, "get_engine", lambda: engine)
        calls = []
        monkeypatch.setattr(trig, "_call_snapshot_endpoint",
                            lambda base_url, api_key: calls.append(1) or (200, _clean_payload(), "{}"))

        exit_code = trig.main()

        assert exit_code == 0
        assert calls == []
        assert _one(engine)["Status"] == srl.STATUS_SKIPPED

    def test_enabled_path_transport_failure_still_returns_0(self, monkeypatch, engine):
        monkeypatch.setenv(trig._ENABLED_ENV, "true")
        monkeypatch.setattr(trig, "get_engine", lambda: engine)
        monkeypatch.setattr(trig, "_call_snapshot_endpoint",
                            _http_post_raising(TimeoutError("timed out")))

        exit_code = trig.main()

        assert exit_code == 0
        assert _one(engine)["Status"] == srl.STATUS_FAILED

    def test_no_db_engine_still_returns_0(self, monkeypatch):
        """DB unreachable: main() must still exit 0 (fail-soft), and it still
        attempts the HTTP call so the server-side pass can run even though this
        attempt goes unaudited locally."""
        monkeypatch.setenv(trig._ENABLED_ENV, "true")

        def _boom():
            raise RuntimeError("no DB configured")
        monkeypatch.setattr(trig, "get_engine", _boom)

        called = []
        monkeypatch.setattr(trig, "_call_snapshot_endpoint",
                            lambda base_url, api_key: called.append(1) or (200, _clean_payload(), "{}"))

        exit_code = trig.main()

        assert exit_code == 0
        assert called == [1]
