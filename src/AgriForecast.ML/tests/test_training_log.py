"""Tests for agriforecast_ml.training_log (admin Logs hub Phase 2, PR B).

Hermetic: no live DB, no real models dir. The upsert / flag-sync SQL is written
as portable UPDATE-then-INSERT + plain UPDATEs (no MERGE / SYSUTCDATETIME), so
the stateful behaviours (insert-then-update on the same version, exactly-one
Promoted=1, rounding/truncation persisted) are verified against a REAL
in-memory SQLite engine -- the strongest available evidence short of SQL Server,
and the same "inject the timestamp instead of a server clock" approach the task
brief allows (TrainedAtUtc is a bound param here, not SYSUTCDATETIME).

The save_model fail-open posture is exercised separately via a deliberately
broken engine driven through registry._record_training_run.

Coverage:
  TestValueCoercion       -- rounding to 2dp, truncation to column caps, bit /
                             int / datetime coercion, all None-safe.
  TestValuesFromMetadata  -- extraction from the save_model metadata dict incl.
                             the older flat-cv fallbacks; DecisionPromoted comes
                             from metadata["promoted"], NOT the live pointer.
  TestUpsert              -- INSERT on first call, UPDATE on the second call for
                             the same version (no duplicate row, no crash); the
                             upsert never touches Promoted.
  TestSyncPromotedFlags   -- exactly one Promoted=1 after sync; idempotent;
                             re-pointing moves the flag; unknown pointer -> all 0;
                             DecisionPromoted/Promoted divergence (v17 case).
  TestFailOpen            -- registry._record_training_run swallows a broken
                             engine with a redacted warning; save_model still
                             returns + writes files.
  TestRedaction           -- redact_sensitive strips DSN fragments + paths.
"""
from __future__ import annotations

import logging
import sys
from datetime import datetime
from pathlib import Path

import pytest
import sqlalchemy as sa

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

from agriforecast_ml import training_log as tl  # noqa: E402


# In-memory SQLite table mirroring the frozen ModelTrainingRuns contract.
# CreatedUtc has a DB default (never set by the writer); Version is UNIQUE.
_CREATE_TABLE = """
CREATE TABLE ModelTrainingRuns (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    Version             TEXT NOT NULL UNIQUE,
    TrainedAtUtc        TIMESTAMP,
    Promoted            INTEGER NOT NULL DEFAULT 0,
    DecisionPromoted    INTEGER,
    PromotionDecision   TEXT,
    BestMlKind          TEXT,
    BestMlMae           REAL,
    BestBaselineKind    TEXT,
    BestBaselineMae     REAL,
    NTrainRows          INTEGER,
    NCrops              INTEGER,
    FeatureContractHash TEXT,
    CreatedUtc          TIMESTAMP DEFAULT CURRENT_TIMESTAMP
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
        res = conn.execute(sa.text("SELECT * FROM ModelTrainingRuns ORDER BY Version"))
        return [dict(r._mapping) for r in res]


def _base_kwargs(**over):
    kw = dict(
        version="v5",
        trained_at="2026-06-01T12:00:00+00:00",
        decision_promoted=True,
        promotion_decision="PROMOTE (hybrid 97.92 beats crop_mean AND incumbent)",
        best_ml_kind="hybrid",
        best_ml_mae=97.921,
        best_baseline_kind="crop_mean",
        best_baseline_mae=100.005,
        n_train_rows=1234,
        n_crops=11,
        feature_contract_hash="abc123",
    )
    kw.update(over)
    return kw


# Value coercion

class TestValueCoercion:
    def test_round2_none_safe(self):
        assert tl._round2(None) is None
        assert tl._round2(97.921) == 97.92
        assert tl._round2(100) == 100.0
        assert tl._round2("nope") is None

    def test_truncate_none_safe_and_caps(self):
        assert tl._truncate(None, 10) is None
        assert tl._truncate("x" * 100, 20) == "x" * 20
        assert tl._truncate("short", 20) == "short"
        # non-str passes through untouched
        assert tl._truncate(5, 20) == 5

    def test_to_bit(self):
        assert tl._to_bit(None) is None
        assert tl._to_bit(True) == 1
        assert tl._to_bit(False) == 0
        assert tl._to_bit(0) == 0

    def test_to_int_none_safe(self):
        assert tl._to_int(None) is None
        assert tl._to_int("7") == 7
        assert tl._to_int(3.9) == 3

    def test_to_datetime_parses_iso(self):
        dt = tl._to_datetime("2026-06-01T12:00:00+00:00")
        assert isinstance(dt, datetime)
        # already-datetime passes through, junk string passes through unchanged
        assert tl._to_datetime(dt) is dt
        assert tl._to_datetime("not-a-date") == "not-a-date"


# values_from_metadata

class TestValuesFromMetadata:
    def test_extracts_gate_keys(self):
        meta = {
            "version": "v9", "trained_at": "2026-05-01T00:00:00+00:00",
            "promoted": False, "promotion_decision": "KEEP incumbent",
            "feature_contract_hash": "hash9", "n_train_rows": 500, "n_crops": 7,
            "cv": {"best_ml": "hybrid", "best_ml_MAE": 80.0,
                   "best_baseline": "carry_forward", "best_baseline_MAE": 70.0},
        }
        v = tl.values_from_metadata(meta)
        assert v["version"] == "v9"
        # DecisionPromoted is the gate verdict = metadata["promoted"], NOT a pointer
        assert v["decision_promoted"] is False
        assert v["best_ml_kind"] == "hybrid"
        assert v["best_ml_mae"] == 80.0
        assert v["best_baseline_kind"] == "carry_forward"
        assert v["best_baseline_mae"] == 70.0
        assert v["n_train_rows"] == 500 and v["n_crops"] == 7

    def test_falls_back_to_flat_cv_keys_for_old_metadata(self):
        meta = {"version": "v1", "promoted": True,
                "cv": {"model_MAE": 120.0, "cropmean_MAE": 130.0}}
        v = tl.values_from_metadata(meta)
        assert v["best_ml_mae"] == 120.0
        assert v["best_baseline_mae"] == 130.0

    def test_missing_cv_is_none_safe(self):
        v = tl.values_from_metadata({"version": "v2", "promoted": True})
        assert v["best_ml_kind"] is None and v["best_ml_mae"] is None
        assert v["best_baseline_mae"] is None


# upsert_training_run

class TestUpsert:
    def test_insert_then_update_same_version_no_duplicate(self, engine):
        tl.upsert_training_run(engine, **_base_kwargs())
        rows = _rows(engine)
        assert len(rows) == 1
        assert rows[0]["Version"] == "v5"
        # values rounded to 2dp
        assert rows[0]["BestMlMae"] == 97.92
        assert rows[0]["BestBaselineMae"] == 100.0
        # a fresh insert seeds Promoted=0 (sync owns the flag)
        assert rows[0]["Promoted"] == 0
        assert rows[0]["DecisionPromoted"] == 1

        # Retrain re-emits the SAME version -> UPDATE, not a crash / duplicate.
        tl.upsert_training_run(engine, **_base_kwargs(
            best_ml_mae=55.5, promotion_decision="re-run", decision_promoted=False))
        rows = _rows(engine)
        assert len(rows) == 1, "re-emitting a version must UPDATE, not duplicate"
        assert rows[0]["BestMlMae"] == 55.5
        assert rows[0]["PromotionDecision"] == "re-run"
        assert rows[0]["DecisionPromoted"] == 0

    def test_upsert_does_not_touch_promoted_on_update(self, engine):
        tl.upsert_training_run(engine, **_base_kwargs(version="v5"))
        # simulate this version being the live pointer
        tl.sync_promoted_flags(engine, "v5")
        assert _rows(engine)[0]["Promoted"] == 1
        # a retrain UPDATE must NOT reset Promoted back to 0
        tl.upsert_training_run(engine, **_base_kwargs(version="v5", best_ml_mae=42.0))
        row = _rows(engine)[0]
        assert row["Promoted"] == 1, "UPDATE path must leave Promoted untouched"
        assert row["BestMlMae"] == 42.0

    def test_truncation_applied(self, engine):
        tl.upsert_training_run(engine, **_base_kwargs(
            version="v" + "9" * 40,            # 41 chars -> cap 20
            best_ml_kind="k" * 80,             # -> cap 50
            promotion_decision="d" * 3000,     # -> cap 2000
            feature_contract_hash="h" * 200,   # -> cap 100
        ))
        row = _rows(engine)[0]
        assert len(row["Version"]) == 20
        assert len(row["BestMlKind"]) == 50
        assert len(row["PromotionDecision"]) == 2000
        assert len(row["FeatureContractHash"]) == 100

    def test_none_values_are_safe(self, engine):
        tl.upsert_training_run(
            engine, version="v3", trained_at=None, decision_promoted=None,
            promotion_decision=None, best_ml_kind=None, best_ml_mae=None,
            best_baseline_kind=None, best_baseline_mae=None, n_train_rows=None,
            n_crops=None, feature_contract_hash=None)
        row = _rows(engine)[0]
        assert row["Version"] == "v3"
        assert row["BestMlMae"] is None and row["DecisionPromoted"] is None


# sync_promoted_flags

class TestSyncPromotedFlags:
    def _seed(self, engine, versions):
        for v in versions:
            tl.upsert_training_run(engine, **_base_kwargs(version=v))

    def test_exactly_one_promoted(self, engine):
        self._seed(engine, ["v1", "v2", "v3"])
        tl.sync_promoted_flags(engine, "v2")
        rows = {r["Version"]: r["Promoted"] for r in _rows(engine)}
        assert rows == {"v1": 0, "v2": 1, "v3": 0}
        assert sum(rows.values()) == 1

    def test_idempotent(self, engine):
        self._seed(engine, ["v1", "v2"])
        tl.sync_promoted_flags(engine, "v2")
        tl.sync_promoted_flags(engine, "v2")
        rows = {r["Version"]: r["Promoted"] for r in _rows(engine)}
        assert rows == {"v1": 0, "v2": 1}

    def test_repointing_moves_the_flag(self, engine):
        self._seed(engine, ["v1", "v2", "v3"])
        tl.sync_promoted_flags(engine, "v2")
        tl.sync_promoted_flags(engine, "v3")
        rows = {r["Version"]: r["Promoted"] for r in _rows(engine)}
        assert rows == {"v1": 0, "v2": 0, "v3": 1}

    def test_unknown_pointer_clears_all(self, engine):
        self._seed(engine, ["v1", "v2"])
        tl.sync_promoted_flags(engine, "v2")
        tl.sync_promoted_flags(engine, "does-not-exist")
        assert all(r["Promoted"] == 0 for r in _rows(engine))

    def test_promoted_and_decision_promoted_diverge_v17_case(self, engine):
        """v17 is the live pointer via a MANUAL override yet its metadata says
        promoted=false. The row must land Promoted=1 (live) AND
        DecisionPromoted=0 (gate verdict) simultaneously."""
        tl.upsert_training_run(engine, **_base_kwargs(version="v16", decision_promoted=True))
        tl.upsert_training_run(engine, **_base_kwargs(version="v17", decision_promoted=False))
        tl.sync_promoted_flags(engine, "v17")  # pointer -> v17 (override)
        rows = {r["Version"]: r for r in _rows(engine)}
        assert rows["v17"]["Promoted"] == 1
        assert rows["v17"]["DecisionPromoted"] == 0
        assert rows["v16"]["Promoted"] == 0
        assert rows["v16"]["DecisionPromoted"] == 1


# Fail-open (registry.save_model hook)

class TestFailOpen:
    def test_record_training_run_swallows_broken_engine(self, monkeypatch, caplog):
        from agriforecast_ml.registry import registry

        class _BoomEngine:
            def begin(self):
                raise RuntimeError("Server=10.0.0.5;Password=hunter2 unreachable")

        monkeypatch.setattr("agriforecast_ml.db.get_engine", lambda: _BoomEngine())
        with caplog.at_level(logging.WARNING):
            # must NOT raise despite the engine blowing up
            registry._record_training_run({"version": "v1", "promoted": True})
        assert "training_log write skipped" in caplog.text
        # redacted: no secret / host leaks into the warning
        assert "hunter2" not in caplog.text
        assert "10.0.0.5" not in caplog.text

    def test_save_model_still_succeeds_when_log_write_fails(self, tmp_path, monkeypatch):
        from agriforecast_ml.registry import registry
        monkeypatch.setattr(registry, "_MODELS_DIR", tmp_path / "models")
        (tmp_path / "models").mkdir()

        def _boom(_metadata):
            raise RuntimeError("db down")
        # A raising hook would be a bug in _record_training_run's own fail-open
        # contract, but save_model must survive even that worst case.
        monkeypatch.setattr(registry, "_record_training_run", _boom)
        try:
            version = registry.save_model({"x": 1}, {"algo": "t"}, promote=True)
        except RuntimeError:
            pytest.fail("save_model must not propagate a training-log hook failure")
        assert (tmp_path / "models" / version / "model.pkl").exists()
        assert (tmp_path / "models" / "promoted.json").exists()


# Redaction

class TestRedaction:
    def test_dsn_and_path_redacted(self):
        out = tl.redact_sensitive("fail Server=10.0.0.5;Password=hunter2 at /Users/x/.env")
        assert "hunter2" not in out and "10.0.0.5" not in out
        assert "/Users/x" not in out
        assert "Password=<redacted>" in out

    def test_benign_unchanged(self):
        assert tl.redact_sensitive("connection timed out") == "connection timed out"


class TestTrainedAtBind:
    """datetime2 has no offset and pymssql's tz-aware bind behavior is exactly
    what the fail-open hook would silently hide if it broke — the bind value
    must always reach the driver as a NAIVE datetime already in UTC."""

    def test_tz_aware_iso_string_normalized_to_naive_utc(self):
        from datetime import datetime
        out = tl._to_datetime("2026-07-21T15:16:27+05:30")
        assert isinstance(out, datetime)
        assert out.tzinfo is None
        assert out == datetime(2026, 7, 21, 9, 46, 27)

    def test_tz_aware_datetime_normalized_to_naive_utc(self):
        from datetime import datetime, timezone
        out = tl._to_datetime(datetime(2026, 7, 21, 15, 16, 27, tzinfo=timezone.utc))
        assert out == datetime(2026, 7, 21, 15, 16, 27)
        assert out.tzinfo is None

    def test_naive_datetime_passes_through(self):
        from datetime import datetime
        naive = datetime(2026, 7, 21, 15, 16, 27)
        assert tl._to_datetime(naive) is naive
