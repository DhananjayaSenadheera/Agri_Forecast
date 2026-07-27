"""Proves the k8s/pipeline-daily.yaml build-features container's shell guard:
trigger_forecast_snapshot.py runs ONLY after build_features.py exits 0, is
SKIPPED entirely when build_features fails, and can never itself fail the Job
(fail-soft, PRD 3.7).

This does not re-implement the guard logic and assert against the copy -- that
would drift silently if the manifest changed underneath it. Instead it EXTRACTS
the real shell block from the real manifest (bounded by the
SNAPSHOT_TRIGGER_SHELL_START/END sentinel comments -- see the manifest's [7b]
header comment) and executes it for real via `sh -c`, with stub `build_features.py`
/ `trigger_forecast_snapshot.py` scripts standing in for the real ones in an
isolated temp directory. `python` on PATH is shimmed to the current interpreter so
this needs no dependency on what "python" resolves to in this environment.
"""
from __future__ import annotations

import os
import stat
import subprocess
import sys
import textwrap
from pathlib import Path

import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = ML_ROOT.parent.parent
PIPELINE_YAML = REPO_ROOT / "k8s" / "pipeline-daily.yaml"

_START_MARKER = "# SNAPSHOT_TRIGGER_SHELL_START"
_END_MARKER = "# SNAPSHOT_TRIGGER_SHELL_END"


def _extract_shell_script() -> str:
    """Pulls the exact shell body between the sentinel markers out of the real
    manifest, dedents it, and returns it ready to hand to `sh -c`."""
    assert PIPELINE_YAML.exists(), f"manifest not found at {PIPELINE_YAML}"
    text = PIPELINE_YAML.read_text(encoding="utf-8")
    start = text.index(_START_MARKER)
    end = text.index(_END_MARKER, start)
    block = text[start:end]
    lines = block.splitlines()

    body_start = None
    for idx, line in enumerate(lines):
        if line.strip() == "- |":
            body_start = idx + 1
            break
    assert body_start is not None, (
        "could not find the '- |' YAML block-scalar start between the sentinel "
        "markers -- the manifest's build-features args block may have been reshaped"
    )
    script = textwrap.dedent("\n".join(lines[body_start:]))
    assert "build_features.py" in script
    assert "trigger_forecast_snapshot.py" in script
    return script


SCRIPT = _extract_shell_script()


def _write_stub(path: Path, exit_code: int, marker_name: "str | None" = None) -> None:
    marker_write = f'Path("{marker_name}").write_text("ran")\n' if marker_name else ""
    path.write_text(
        "import sys\n"
        "from pathlib import Path\n"
        f"{marker_write}"
        f"sys.exit({exit_code})\n",
        encoding="utf-8",
    )


@pytest.fixture()
def sandbox(tmp_path: Path) -> Path:
    """A temp cwd with a `python` shim on PATH pointing at the current
    interpreter, so the extracted script's `python build_features.py` /
    `python trigger_forecast_snapshot.py` calls resolve deterministically."""
    bin_dir = tmp_path / "bin"
    bin_dir.mkdir()
    shim = bin_dir / "python"
    shim.write_text(f'#!/bin/sh\nexec "{sys.executable}" "$@"\n', encoding="utf-8")
    shim.chmod(shim.stat().st_mode | stat.S_IEXEC | stat.S_IXGRP | stat.S_IXOTH)
    return tmp_path


def _run(sandbox: Path) -> subprocess.CompletedProcess:
    env = dict(os.environ)
    env["PATH"] = f"{sandbox / 'bin'}{os.pathsep}{env.get('PATH', '')}"
    return subprocess.run(
        ["sh", "-c", SCRIPT],
        cwd=sandbox,
        env=env,
        capture_output=True,
        text=True,
        timeout=30,
    )


class TestBuildSucceeds:
    def test_trigger_runs_after_a_successful_build(self, sandbox: Path):
        _write_stub(sandbox / "build_features.py", exit_code=0)
        _write_stub(sandbox / "trigger_forecast_snapshot.py", exit_code=0, marker_name="trigger_ran.txt")

        result = _run(sandbox)

        assert result.returncode == 0
        assert (sandbox / "trigger_ran.txt").exists()

    def test_trigger_failing_never_fails_the_job(self, sandbox: Path):
        """Fail-soft belt-and-suspenders: even if trigger_forecast_snapshot.py somehow
        exits non-zero (it should not, by its own construction), the `|| true` means
        the container's overall exit code still reflects build_features alone."""
        _write_stub(sandbox / "build_features.py", exit_code=0)
        _write_stub(sandbox / "trigger_forecast_snapshot.py", exit_code=7, marker_name="trigger_ran.txt")

        result = _run(sandbox)

        assert result.returncode == 0, "a failing trigger step must not fail the pipeline Job"
        assert (sandbox / "trigger_ran.txt").exists(), "the trigger must still have been invoked"


class TestBuildFails:
    def test_trigger_is_skipped_and_build_exit_code_propagates(self, sandbox: Path):
        _write_stub(sandbox / "build_features.py", exit_code=3)
        _write_stub(sandbox / "trigger_forecast_snapshot.py", exit_code=0, marker_name="trigger_ran.txt")

        result = _run(sandbox)

        assert result.returncode == 3, "a failed build must still fail the Job (its own exit code)"
        assert not (sandbox / "trigger_ran.txt").exists(), (
            "trigger_forecast_snapshot.py must never run when build_features failed"
        )
        assert "skipping the forecast-snapshot trigger" in result.stderr
