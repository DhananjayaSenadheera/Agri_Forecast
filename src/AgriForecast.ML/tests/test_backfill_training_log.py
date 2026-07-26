"""Tests for backfill_training_log.py (admin Logs hub Phase 2, PR B).

Hermetic: synthetic models dirs in tmp_path (NEVER the real registry), an
in-memory SQLite engine injected in place of get_engine, and a stubbed
load_env_file. No live DB, no real models, nothing printed that could be a DSN.

Coverage:
  TestCollectVersions  -- reads metadata.json + promoted.json; Promoted resolved
                          against the pointer; DecisionPromoted from each
                          metadata's promoted field; numeric version sort;
                          skips dirs without metadata.
  TestDivergence       -- the pointer version whose OWN metadata says
                          promoted=false lands Promoted=1 / DecisionPromoted=0.
  TestWriteAndIdempotency -- main() writes one row per version; a second run is
                          a no-op (no duplicates); flags stay correct.
  TestDryRun           -- --dry-run prints the summary but NEVER touches the DB.
  TestExitCodes        -- missing models dir -> 2; empty registry -> 0 no-write.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path

import pytest
import sqlalchemy as sa

ML_ROOT = Path(__file__).resolve().parents[1]
if str(ML_ROOT) not in sys.path:
    sys.path.insert(0, str(ML_ROOT))

import backfill_training_log as bf  # noqa: E402

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


def _write_version(models_dir: Path, version: str, *, promoted: bool,
                   best_ml="hybrid", best_ml_mae=97.92):
    vdir = models_dir / version
    vdir.mkdir(parents=True)
    (vdir / "metadata.json").write_text(json.dumps({
        "version": version,
        "trained_at": "2026-06-01T00:00:00+00:00",
        "promoted": promoted,              # == DecisionPromoted (gate verdict)
        "promotion_decision": f"decision for {version}",
        "feature_contract_hash": f"hash-{version}",
        "n_train_rows": 100, "n_crops": 11,
        "cv": {"best_ml": best_ml, "best_ml_MAE": best_ml_mae,
               "best_baseline": "crop_mean", "best_baseline_MAE": 100.0},
    }))


def _make_models_dir(tmp_path: Path, versions, pointer) -> Path:
    """versions: list of (name, metadata_promoted_bool). pointer: live version."""
    models_dir = tmp_path / "models"
    models_dir.mkdir()
    for name, promoted in versions:
        _write_version(models_dir, name, promoted=promoted)
    (models_dir / "promoted.json").write_text(json.dumps({"version": pointer}))
    return models_dir


@pytest.fixture()
def sqlite_engine():
    eng = sa.create_engine("sqlite://")
    with eng.begin() as conn:
        conn.execute(sa.text(_CREATE_TABLE))
    return eng


def _db_rows(engine):
    with engine.connect() as conn:
        res = conn.execute(sa.text("SELECT * FROM ModelTrainingRuns ORDER BY Version"))
        return {r._mapping["Version"]: dict(r._mapping) for r in res}


# collect_versions

class TestCollectVersions:
    def test_reads_and_resolves_pointer(self, tmp_path):
        md = _make_models_dir(
            tmp_path,
            [("v1", True), ("v2", True), ("v10", True)],
            pointer="v10",
        )
        rows, pointer = bf.collect_versions(md)
        assert pointer == "v10"
        # numeric sort: v1, v2, v10 (not lexical v1, v10, v2)
        assert [r["version"] for r in rows] == ["v1", "v2", "v10"]
        promoted = {r["version"]: r["_promoted"] for r in rows}
        assert promoted == {"v1": 0, "v2": 0, "v10": 1}
        decision = {r["version"]: r["decision_promoted"] for r in rows}
        assert decision == {"v1": True, "v2": True, "v10": True}

    def test_skips_dir_without_metadata(self, tmp_path):
        md = _make_models_dir(tmp_path, [("v1", True)], pointer="v1")
        (md / "v2").mkdir()  # no metadata.json
        rows, _ = bf.collect_versions(md)
        assert [r["version"] for r in rows] == ["v1"]

    def test_missing_dir_raises(self, tmp_path):
        with pytest.raises(FileNotFoundError):
            bf.collect_versions(tmp_path / "nope")


# Divergence (v17-style override)

class TestDivergence:
    def test_pointer_version_with_false_metadata_diverges(self, tmp_path):
        # v17 metadata says promoted=false, yet promoted.json points at it.
        md = _make_models_dir(
            tmp_path,
            [("v16", True), ("v17", False)],
            pointer="v17",
        )
        rows, pointer = bf.collect_versions(md)
        by_v = {r["version"]: r for r in rows}
        assert by_v["v17"]["_promoted"] == 1          # live pointer
        assert by_v["v17"]["decision_promoted"] is False   # gate verdict
        assert by_v["v16"]["_promoted"] == 0
        assert by_v["v16"]["decision_promoted"] is True


# Write + idempotency via main()

class TestWriteAndIdempotency:
    def _run(self, monkeypatch, engine, models_dir, extra_argv=()):
        monkeypatch.setattr(bf, "load_env_file", lambda: None)
        monkeypatch.setattr(bf, "get_engine", lambda: engine)
        argv = ["backfill_training_log.py", "--models-dir", str(models_dir), *extra_argv]
        monkeypatch.setattr(sys, "argv", argv)
        return bf.main()

    def test_writes_one_row_per_version(self, tmp_path, sqlite_engine, monkeypatch):
        md = _make_models_dir(
            tmp_path, [("v1", True), ("v2", False)], pointer="v2")
        rc = self._run(monkeypatch, sqlite_engine, md)
        assert rc == 0
        rows = _db_rows(sqlite_engine)
        assert set(rows) == {"v1", "v2"}
        # v2 is the pointer whose metadata says promoted=false -> divergence
        assert rows["v2"]["Promoted"] == 1
        assert rows["v2"]["DecisionPromoted"] == 0
        assert rows["v1"]["Promoted"] == 0
        assert rows["v1"]["DecisionPromoted"] == 1
        # exactly one Promoted=1
        assert sum(r["Promoted"] for r in rows.values()) == 1

    def test_rerun_is_idempotent(self, tmp_path, sqlite_engine, monkeypatch):
        md = _make_models_dir(
            tmp_path, [("v1", True), ("v2", True)], pointer="v2")
        assert self._run(monkeypatch, sqlite_engine, md) == 0
        first = _db_rows(sqlite_engine)
        assert self._run(monkeypatch, sqlite_engine, md) == 0
        second = _db_rows(sqlite_engine)
        # same versions, no duplicates, same Ids (UPDATE not re-INSERT)
        assert set(first) == set(second) == {"v1", "v2"}
        assert {v: r["Id"] for v, r in first.items()} == {v: r["Id"] for v, r in second.items()}
        assert sum(r["Promoted"] for r in second.values()) == 1


# --dry-run

class TestDryRun:
    def test_dry_run_never_touches_db(self, tmp_path, monkeypatch, capsys):
        md = _make_models_dir(tmp_path, [("v1", True)], pointer="v1")

        def _boom_engine():
            raise AssertionError("get_engine must NOT be called on --dry-run")

        monkeypatch.setattr(bf, "get_engine", _boom_engine)
        monkeypatch.setattr(bf, "load_env_file", lambda: None)
        monkeypatch.setattr(sys, "argv", [
            "backfill_training_log.py", "--models-dir", str(md), "--dry-run"])
        rc = bf.main()
        assert rc == 0
        out = capsys.readouterr().out
        assert "DRY-RUN" in out
        assert "nothing written" in out


# Exit codes

class TestExitCodes:
    def test_missing_models_dir_exits_2(self, tmp_path, monkeypatch, capsys):
        monkeypatch.setattr(sys, "argv", [
            "backfill_training_log.py", "--models-dir", str(tmp_path / "nope")])
        assert bf.main() == 2
        assert "could not read the model registry" in capsys.readouterr().out

    def test_empty_registry_writes_nothing_exit_0(self, tmp_path, monkeypatch, capsys):
        models_dir = tmp_path / "models"
        models_dir.mkdir()  # exists but has no version dirs

        def _boom_engine():
            raise AssertionError("no versions -> must not open a DB connection")

        monkeypatch.setattr(bf, "get_engine", _boom_engine)
        monkeypatch.setattr(bf, "load_env_file", lambda: None)
        monkeypatch.setattr(sys, "argv", [
            "backfill_training_log.py", "--models-dir", str(models_dir)])
        assert bf.main() == 0
        assert "nothing written" in capsys.readouterr().out
