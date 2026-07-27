"""Tests for build_features.py's FEATURE_BUILD audit-row wiring
(agriforecast_ml.feature_run_log integration).

Hermetic: no live DB, no live pipeline. The pipeline stages (load.*,
features.build_all, store.write_features) and the audit primitives
(get_engine, feature_run_log.start_run/mark_succeeded/mark_failed) are all
monkeypatched to fakes -- mirroring test_training_log.py's TestFailOpen
posture (a deliberately-broken collaborator, asserted to never propagate).

Coverage:
  TestStartAuditRun          -- returns (engine, run_id) on success; (None, None)
                                 fail-open on a broken get_engine / start_run.
  TestFinishAuditRunSucceeded -- no-op when engine/run_id is None; forwards
                                  kwargs; swallows a broken mark_succeeded.
  TestFinishAuditRunFailed    -- no-op when engine/run_id is None; forwards the
                                  exception; swallows a broken mark_failed.
  TestRunQaGate               -- the post-persist QA gate in isolation: a
                                  failed check raises naming qa_features; an
                                  all-pass result is a silent no-op; a CRASH
                                  inside qa_features.run_qa() itself is
                                  fail-open (logged, does not raise).
  TestMainAuditIntegration    -- end-to-end main(): success path calls
                                  mark_succeeded with the right
                                  rows_inserted/distinct_crops/coverage (QA
                                  gate mocked to all-pass); a pipeline
                                  exception calls mark_failed AND still
                                  re-raises; a QA check FAILURE (after a
                                  successful persist) calls mark_failed with a
                                  qa_features-naming reason AND re-raises; a
                                  QA harness CRASH is fail-open and still
                                  records Succeeded; a totally broken audit
                                  layer never blocks a successful build.
"""
from __future__ import annotations

import sys
from datetime import date
from pathlib import Path

import pandas as pd
import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

import build_features  # noqa: E402


# _start_audit_run

class TestStartAuditRun:
    def test_success_returns_engine_and_run_id(self, monkeypatch):
        sentinel_engine = object()
        sentinel_run_id = "run-1"
        monkeypatch.setattr(build_features, "get_engine", lambda: sentinel_engine)
        monkeypatch.setattr(
            build_features.feature_run_log, "start_run",
            lambda engine: (sentinel_run_id, "batch-1", "started"),
        )
        engine, run_id = build_features._start_audit_run()
        assert engine is sentinel_engine
        assert run_id == sentinel_run_id

    def test_broken_get_engine_fails_open(self, monkeypatch, capsys):
        def _boom():
            raise RuntimeError("no AGRI_DB_* configured")
        monkeypatch.setattr(build_features, "get_engine", _boom)
        engine, run_id = build_features._start_audit_run()
        assert (engine, run_id) == (None, None)
        assert "feature_run_log" in capsys.readouterr().err

    def test_broken_start_run_fails_open(self, monkeypatch, capsys):
        monkeypatch.setattr(build_features, "get_engine", lambda: object())

        def _boom(engine):
            raise RuntimeError("table IngestionRuns not found")
        monkeypatch.setattr(build_features.feature_run_log, "start_run", _boom)
        engine, run_id = build_features._start_audit_run()
        assert (engine, run_id) == (None, None)
        assert "feature_run_log" in capsys.readouterr().err


# _finish_audit_run_succeeded

class TestFinishAuditRunSucceeded:
    def test_noop_when_engine_is_none(self, monkeypatch):
        calls = []
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_succeeded",
            lambda *a, **k: calls.append((a, k)),
        )
        build_features._finish_audit_run_succeeded(None, None, rows_inserted=5)
        assert calls == []

    def test_forwards_kwargs(self, monkeypatch):
        calls = []
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_succeeded",
            lambda engine, run_id, **k: calls.append((engine, run_id, k)),
        )
        build_features._finish_audit_run_succeeded(
            "eng", "run-1", rows_inserted=100, distinct_crops=9,
            covered_from=date(2015, 6, 22), covered_to=date(2026, 7, 25),
        )
        assert len(calls) == 1
        engine, run_id, kwargs = calls[0]
        assert engine == "eng" and run_id == "run-1"
        assert kwargs == {
            "rows_inserted": 100, "distinct_crops": 9,
            "covered_from": date(2015, 6, 22), "covered_to": date(2026, 7, 25),
        }

    def test_broken_mark_succeeded_fails_open(self, monkeypatch, capsys):
        def _boom(*a, **k):
            raise RuntimeError("connection reset")
        monkeypatch.setattr(build_features.feature_run_log, "mark_succeeded", _boom)
        # must not raise
        build_features._finish_audit_run_succeeded("eng", "run-1", rows_inserted=1)
        assert "feature_run_log" in capsys.readouterr().err


# _finish_audit_run_failed

class TestFinishAuditRunFailed:
    def test_noop_when_engine_is_none(self, monkeypatch):
        calls = []
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_failed",
            lambda *a, **k: calls.append((a, k)),
        )
        build_features._finish_audit_run_failed(None, None, ValueError("x"))
        assert calls == []

    def test_forwards_exception(self, monkeypatch):
        calls = []
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_failed",
            lambda engine, run_id, exc: calls.append((engine, run_id, exc)),
        )
        exc = ValueError("weather join produced 0 rows")
        build_features._finish_audit_run_failed("eng", "run-1", exc)
        assert calls == [("eng", "run-1", exc)]

    def test_broken_mark_failed_never_masks_original_error(self, monkeypatch, capsys):
        def _boom(*a, **k):
            raise RuntimeError("connection reset")
        monkeypatch.setattr(build_features.feature_run_log, "mark_failed", _boom)
        # must not raise (the ORIGINAL build exception is re-raised by main(),
        # not by this helper)
        build_features._finish_audit_run_failed("eng", "run-1", ValueError("boom"))
        assert "feature_run_log" in capsys.readouterr().err


# _run_qa_gate

class TestRunQaGate:
    def test_all_pass_is_a_silent_noop(self, monkeypatch):
        monkeypatch.setattr(build_features.qa_features, "run_qa",
                             lambda: (["c1", "c2"], []))
        build_features._run_qa_gate()  # must not raise

    def test_a_failed_check_raises_naming_qa_features_and_the_checks(self, monkeypatch):
        monkeypatch.setattr(
            build_features.qa_features, "run_qa",
            lambda: (["c1"], ["all contract columns present, none extra"]),
        )
        with pytest.raises(RuntimeError, match="qa_features") as exc_info:
            build_features._run_qa_gate()
        assert "1 check" in str(exc_info.value)
        assert "all contract columns present, none extra" in str(exc_info.value)

    def test_multiple_failed_checks_all_named(self, monkeypatch):
        monkeypatch.setattr(
            build_features.qa_features, "run_qa",
            lambda: ([], ["row count > 0", "no duplicate (crop,date) keys"]),
        )
        with pytest.raises(RuntimeError) as exc_info:
            build_features._run_qa_gate()
        msg = str(exc_info.value)
        assert "row count > 0" in msg
        assert "no duplicate (crop,date) keys" in msg

    def test_harness_crash_is_fail_open_and_does_not_raise(self, monkeypatch, capsys):
        def _boom():
            raise RuntimeError("qa_features has a bug, not a failed check")
        monkeypatch.setattr(build_features.qa_features, "run_qa", _boom)
        build_features._run_qa_gate()  # must not raise
        assert "qa_features" in capsys.readouterr().err


# main() end-to-end audit wiring

def _fake_feats() -> pd.DataFrame:
    return pd.DataFrame({
        "ObservationDate": pd.to_datetime(["2026-07-01", "2026-07-03", "2026-07-02"]),
        "CropId": ["VEG000001", "VEG000001", "VEG000002"],
        "LabelAvailable": [1, 0, 1],
        "SomeFeature": [1.0, 2.0, 3.0],
    })


def _patch_pipeline(monkeypatch, feats: pd.DataFrame, written: int,
                     qa_result=(["fake-qa-check"], [])):
    """qa_result defaults to an all-pass (passed, failed) tuple so every
    existing test that doesn't care about QA specifically still reaches
    mark_succeeded, exactly as before this gate existed. Tests that DO care
    pass their own (passed, failed) or monkeypatch run_qa to raise."""
    empty_df = pd.DataFrame()
    monkeypatch.setattr(build_features.load, "load_prices",
                         lambda: pd.DataFrame({"CropId": ["VEG000001"]}))
    monkeypatch.setattr(build_features.load, "load_crops", lambda: pd.DataFrame())
    monkeypatch.setattr(build_features.load, "load_weather", lambda: empty_df)
    monkeypatch.setattr(build_features.load, "load_fx", lambda: empty_df)
    monkeypatch.setattr(build_features.load, "load_news_sentiment", lambda: empty_df)
    monkeypatch.setattr(build_features.load, "load_policy_flags", lambda: empty_df)
    monkeypatch.setattr(build_features.load, "load_festivals", lambda: empty_df)
    monkeypatch.setattr(build_features.load, "load_macro_series", lambda: empty_df)
    monkeypatch.setattr(build_features.load, "load_price_observations", lambda: empty_df)
    monkeypatch.setattr(build_features.load, "feature_safe_market_slugs", lambda: [])
    monkeypatch.setattr(build_features.features, "build_all",
                         lambda *a, **k: feats)
    monkeypatch.setattr(build_features.store, "write_features", lambda df: written)
    monkeypatch.setattr(build_features.qa_features, "run_qa", lambda: qa_result)


class TestMainAuditIntegration:
    def test_success_path_records_succeeded_row(self, monkeypatch):
        feats = _fake_feats()
        _patch_pipeline(monkeypatch, feats, written=3)
        monkeypatch.setattr(build_features, "load_env_file", lambda: None)

        start_calls = []
        succeeded_calls = []
        monkeypatch.setattr(build_features, "get_engine", lambda: "the-engine")
        monkeypatch.setattr(
            build_features.feature_run_log, "start_run",
            lambda engine: start_calls.append(engine) or ("run-1", "batch-1", "started"),
        )
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_succeeded",
            lambda engine, run_id, **k: succeeded_calls.append((engine, run_id, k)),
        )
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_failed",
            lambda *a, **k: pytest.fail("mark_failed must not be called on success"),
        )

        build_features.main()

        assert start_calls == ["the-engine"]
        assert len(succeeded_calls) == 1
        engine, run_id, kwargs = succeeded_calls[0]
        assert engine == "the-engine" and run_id == "run-1"
        assert kwargs["rows_inserted"] == 3
        assert kwargs["distinct_crops"] == 2  # VEG000001, VEG000002
        assert kwargs["covered_from"] == date(2026, 7, 1)
        assert kwargs["covered_to"] == date(2026, 7, 3)

    def test_metrics_derivation_failure_still_records_succeeded_and_does_not_raise(
        self, monkeypatch,
    ):
        """store.write_features already succeeded (the build itself is fine) --
        a bug in the LATER coverage/crop-count derivation (min/max ObservationDate,
        CropId.nunique()) must not turn that success into a false Failed row or a
        non-zero exit. Succeeded is still recorded, with the un-derivable metrics
        falling back to None; RowsInserted (known independently of the metrics
        block) stays correct."""
        feats = _fake_feats()
        _patch_pipeline(monkeypatch, feats, written=3)
        monkeypatch.setattr(build_features, "load_env_file", lambda: None)

        def _boom_to_datetime(*a, **k):
            raise RuntimeError("ObservationDate parse blew up")
        monkeypatch.setattr(build_features.pd, "to_datetime", _boom_to_datetime)

        monkeypatch.setattr(build_features, "get_engine", lambda: "the-engine")
        monkeypatch.setattr(
            build_features.feature_run_log, "start_run",
            lambda engine: ("run-1", "batch-1", "started"),
        )
        succeeded_calls = []
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_succeeded",
            lambda engine, run_id, **k: succeeded_calls.append((engine, run_id, k)),
        )
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_failed",
            lambda *a, **k: pytest.fail(
                "mark_failed must not be called: the build itself succeeded"),
        )

        build_features.main()  # must not raise -- exit 0

        assert len(succeeded_calls) == 1
        engine, run_id, kwargs = succeeded_calls[0]
        assert engine == "the-engine" and run_id == "run-1"
        assert kwargs["rows_inserted"] == 3  # known independently, unaffected
        assert kwargs["distinct_crops"] is None
        assert kwargs["covered_from"] is None
        assert kwargs["covered_to"] is None

    def test_pipeline_exception_records_failed_row_and_reraises(self, monkeypatch):
        _patch_pipeline(monkeypatch, _fake_feats(), written=3)
        monkeypatch.setattr(build_features, "load_env_file", lambda: None)

        def _boom(*a, **k):
            raise RuntimeError("feature build blew up")
        monkeypatch.setattr(build_features.features, "build_all", _boom)

        monkeypatch.setattr(build_features, "get_engine", lambda: "the-engine")
        monkeypatch.setattr(
            build_features.feature_run_log, "start_run",
            lambda engine: ("run-1", "batch-1", "started"),
        )
        failed_calls = []
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_failed",
            lambda engine, run_id, exc: failed_calls.append((engine, run_id, exc)),
        )
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_succeeded",
            lambda *a, **k: pytest.fail("mark_succeeded must not be called on failure"),
        )

        with pytest.raises(RuntimeError, match="feature build blew up"):
            build_features.main()

        assert len(failed_calls) == 1
        engine, run_id, exc = failed_calls[0]
        assert engine == "the-engine" and run_id == "run-1"
        assert str(exc) == "feature build blew up"

    def test_qa_failure_after_successful_persist_records_failed_row_and_reraises(
        self, monkeypatch,
    ):
        """The write to CropFeatureDaily succeeded (store.write_features ran
        clean), but qa_features.run_qa() found a real, deliberate check
        failure against what was just persisted. That must still flip the
        FEATURE_BUILD row to Failed (naming qa_features + the failed check)
        and re-raise -- this is the whole point of the gate: a bad build
        must read Failed, not green, on the admin runs table and the
        pipeline health banner."""
        _patch_pipeline(
            monkeypatch, _fake_feats(), written=3,
            qa_result=([], ["all contract columns present, none extra"]),
        )
        monkeypatch.setattr(build_features, "load_env_file", lambda: None)

        monkeypatch.setattr(build_features, "get_engine", lambda: "the-engine")
        monkeypatch.setattr(
            build_features.feature_run_log, "start_run",
            lambda engine: ("run-1", "batch-1", "started"),
        )
        failed_calls = []
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_failed",
            lambda engine, run_id, exc: failed_calls.append((engine, run_id, exc)),
        )
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_succeeded",
            lambda *a, **k: pytest.fail(
                "mark_succeeded must not be called: QA found a real failed check"),
        )

        with pytest.raises(RuntimeError, match="qa_features"):
            build_features.main()

        assert len(failed_calls) == 1
        engine, run_id, exc = failed_calls[0]
        assert engine == "the-engine" and run_id == "run-1"
        assert "qa_features" in str(exc)
        assert "all contract columns present, none extra" in str(exc)

    def test_qa_pass_after_successful_persist_records_succeeded_row(self, monkeypatch):
        """The inverse of the above: all QA checks pass, so the run still
        records Succeeded exactly as it would have before this gate existed.
        (test_success_path_records_succeeded_row already covers this via the
        _patch_pipeline default, but this test names the QA-specific
        behaviour explicitly, matching the reviewer's requested coverage.)"""
        _patch_pipeline(
            monkeypatch, _fake_feats(), written=3,
            qa_result=(["c1", "c2", "c3"], []),
        )
        monkeypatch.setattr(build_features, "load_env_file", lambda: None)

        monkeypatch.setattr(build_features, "get_engine", lambda: "the-engine")
        monkeypatch.setattr(
            build_features.feature_run_log, "start_run",
            lambda engine: ("run-1", "batch-1", "started"),
        )
        succeeded_calls = []
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_succeeded",
            lambda engine, run_id, **k: succeeded_calls.append((engine, run_id, k)),
        )
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_failed",
            lambda *a, **k: pytest.fail("mark_failed must not be called: QA all passed"),
        )

        build_features.main()  # must not raise -- exit 0

        assert len(succeeded_calls) == 1
        assert succeeded_calls[0][2]["rows_inserted"] == 3

    def test_qa_harness_crash_is_fail_open_and_still_records_succeeded(self, monkeypatch):
        """qa_features.run_qa() itself raises (a bug in the harness, not a
        deliberately failed check). The already-persisted, otherwise-correct
        build must NOT be recorded as Failed over a QA harness defect --
        mark_succeeded still fires."""
        _patch_pipeline(monkeypatch, _fake_feats(), written=3)

        def _qa_boom():
            raise AttributeError("qa_features has a bug, not a failed check")
        monkeypatch.setattr(build_features.qa_features, "run_qa", _qa_boom)
        monkeypatch.setattr(build_features, "load_env_file", lambda: None)

        monkeypatch.setattr(build_features, "get_engine", lambda: "the-engine")
        monkeypatch.setattr(
            build_features.feature_run_log, "start_run",
            lambda engine: ("run-1", "batch-1", "started"),
        )
        succeeded_calls = []
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_succeeded",
            lambda engine, run_id, **k: succeeded_calls.append((engine, run_id, k)),
        )
        monkeypatch.setattr(
            build_features.feature_run_log, "mark_failed",
            lambda *a, **k: pytest.fail(
                "mark_failed must not be called: a QA harness crash is fail-open"),
        )

        build_features.main()  # must not raise -- exit 0

        assert len(succeeded_calls) == 1
        assert succeeded_calls[0][2]["rows_inserted"] == 3

    def test_totally_broken_audit_layer_never_blocks_a_successful_build(self, monkeypatch):
        """No DB configured at all: get_engine raises immediately. The build
        must still run to completion and return normally."""
        feats = _fake_feats()
        _patch_pipeline(monkeypatch, feats, written=3)
        monkeypatch.setattr(build_features, "load_env_file", lambda: None)

        def _no_db():
            raise RuntimeError("No DB settings: set AGRI_DB_* env vars")
        monkeypatch.setattr(build_features, "get_engine", _no_db)

        build_features.main()  # must not raise
