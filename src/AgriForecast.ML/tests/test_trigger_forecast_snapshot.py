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


# --- S2: a missing/malformed `errors` block must never read as clean ------------------

class TestMalformedErrorsBlock:
    def test_missing_errors_block_marks_failed_not_succeeded(self, engine):
        payload = _clean_payload()
        del payload["errors"]

        outcome = trig.run(engine, "http://x", "k", http_post=_http_post_returning(200, payload))

        assert outcome == "failed"
        row = _one(engine)
        assert row["Status"] == srl.STATUS_FAILED
        assert "Malformed response" in row["ErrorSummary"]

    def test_errors_block_as_a_list_does_not_raise_and_marks_failed(self, engine):
        """S1's exact reproduction case: 'errors' present but not an object -- a bare
        `.get()` on it would raise AttributeError out of run()."""
        payload = _clean_payload(errors=["not", "a", "dict"])

        outcome = trig.run(engine, "http://x", "k", http_post=_http_post_returning(200, payload))

        assert outcome == "failed"
        assert "Malformed response" in _one(engine)["ErrorSummary"]

    def test_non_integer_failure_count_marks_failed(self, engine):
        payload = _clean_payload(errors={"snapshotCropFailures": "0", "matureRowFailures": 0})

        outcome = trig.run(engine, "http://x", "k", http_post=_http_post_returning(200, payload))

        assert outcome == "failed"
        assert "Malformed response" in _one(engine)["ErrorSummary"]

    def test_missing_one_of_the_two_counters_marks_failed(self, engine):
        payload = _clean_payload(errors={"snapshotCropFailures": 0})  # matureRowFailures absent

        outcome = trig.run(engine, "http://x", "k", http_post=_http_post_returning(200, payload))

        assert outcome == "failed"
        assert "Malformed response" in _one(engine)["ErrorSummary"]

    def test_boolean_failure_count_marks_failed(self, engine):
        """bool is an int subclass in Python -- a JSON true/false must still be rejected as
        not a count, or `errors.get('snapshotCropFailures') or 0`-style laxness would
        silently treat `false` as a clean 0."""
        payload = _clean_payload(errors={"snapshotCropFailures": False, "matureRowFailures": 0})

        outcome = trig.run(engine, "http://x", "k", http_post=_http_post_returning(200, payload))

        assert outcome == "failed"
        assert "Malformed response" in _one(engine)["ErrorSummary"]

    def test_genuinely_present_integer_zero_counts_still_succeed(self, engine):
        """Non-vacuity: the malformed-response guard must not reject the legitimate,
        common case of two real zero counts."""
        payload = _clean_payload(errors={"snapshotCropFailures": 0, "matureRowFailures": 0})

        outcome = trig.run(engine, "http://x", "k", http_post=_http_post_returning(200, payload))

        assert outcome == "succeeded"
        assert _one(engine)["Status"] == srl.STATUS_SUCCEEDED


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


# --- B3: http_post must be LATE-bound, not bound once at def time ---------------------

class TestLateBoundHttpPost:
    """run()'s http_post parameter MUST resolve `_call_snapshot_endpoint` fresh on every
    call (`http_post = http_post if http_post is not None else _call_snapshot_endpoint`
    inside the function body), never as a `= _call_snapshot_endpoint` signature default.
    A signature default is evaluated ONCE at module-import time and freezes onto whatever
    object `_call_snapshot_endpoint` was then -- a test's
    monkeypatch.setattr(trig, "_call_snapshot_endpoint", fake) then silently fails to
    take effect for any caller that omits the http_post kwarg (main() does, in
    production), and the REAL urllib call fires instead. This class proves the fix by
    calling run() the way main() does -- with no explicit http_post -- and asserting the
    monkeypatched stub was actually invoked (call-count), not just that some outcome
    resulted (a real transport failure against a closed local port would also produce
    "failed", silently masking the exact bug this guards against).
    """

    def test_run_without_explicit_http_post_uses_the_current_module_attribute(self, monkeypatch, engine):
        calls = []

        def _fake(base_url, api_key):
            calls.append((base_url, api_key))
            return 200, _clean_payload(), "{}"

        monkeypatch.setattr(trig, "_call_snapshot_endpoint", _fake)

        outcome = trig.run(engine, "http://ml-serving:8077", "secret")  # no http_post kwarg, like main()

        assert outcome == "succeeded"
        assert calls == [("http://ml-serving:8077", "secret")], (
            "run() must call the CURRENT trig._call_snapshot_endpoint, not a stale "
            "def-time-bound default"
        )

    def test_monkeypatch_after_a_prior_run_call_still_takes_effect(self, monkeypatch, engine):
        """Non-vacuity check the other direction: patch AFTER run() has already been
        imported and even called once, and prove the NEW patch is honoured too -- a
        def-time-bound default would freeze on the FIRST value seen and never update."""
        first_calls = []
        monkeypatch.setattr(trig, "_call_snapshot_endpoint",
                            lambda base_url, api_key: (first_calls.append(1) or (200, _clean_payload(), "{}")))
        trig.run(engine, "http://x", "k")
        assert first_calls == [1]

        second_calls = []
        monkeypatch.setattr(trig, "_call_snapshot_endpoint",
                            lambda base_url, api_key: (second_calls.append(1) or (500, None, "err")))
        outcome = trig.run(engine, "http://x", "k")

        assert outcome == "failed"
        assert second_calls == [1], "the SECOND monkeypatch must be the one actually invoked"


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
        calls = []

        def _raise(base_url, api_key):
            calls.append((base_url, api_key))
            raise TimeoutError("timed out")

        monkeypatch.setattr(trig, "_call_snapshot_endpoint", _raise)

        exit_code = trig.main()

        assert exit_code == 0
        # B3: call-count, not just the resulting status -- proves main() -> run() actually invoked the
        # monkeypatched stub rather than a stale def-time-bound default silently hitting the real network.
        assert len(calls) == 1
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


# --- S1: main() is the outermost fail-soft net, not just its anticipated branches ------

class TestMainOuterFailSoft:
    """main() wraps _main_impl() in a bare try/except Exception -- proves that ANY
    unanticipated exception escaping the implementation (not just the specific shapes
    run()/S2 already guard against) is swallowed here, so main() truly can never raise
    and never returns non-zero, by construction rather than by enumeration."""

    def test_unexpected_exception_in_main_impl_is_swallowed_and_returns_0(self, monkeypatch):
        def _boom():
            raise RuntimeError("something nobody anticipated")

        monkeypatch.setattr(trig, "_main_impl", _boom)

        exit_code = trig.main()

        assert exit_code == 0

    def test_main_impl_still_runs_normally_when_not_patched(self, monkeypatch, engine):
        """Non-vacuity: the outer try/except must not swallow a NORMAL successful run --
        it is a safety net around _main_impl, not a replacement for it."""
        monkeypatch.setenv(trig._ENABLED_ENV, "true")
        monkeypatch.setattr(trig, "get_engine", lambda: engine)
        monkeypatch.setattr(trig, "_call_snapshot_endpoint",
                            lambda base_url, api_key: (200, _clean_payload(), "{}"))

        exit_code = trig.main()

        assert exit_code == 0
        assert _one(engine)["Status"] == srl.STATUS_SUCCEEDED
