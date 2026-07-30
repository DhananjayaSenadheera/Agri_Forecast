"""Static (no-Docker) guard against the "missing COPY" bug class.

This has now bitten the image FOUR times -- the latest, ingest_cbsl_service.py,
made /admin/ingest-cbsl 503 every night for 4 nights, because it is only
lazily `import`'d inside the endpoint (serving/app.py), so a missing COPY
never surfaces at container startup, only at request time. There was no test
guarding this before.

Pure text/regex analysis over the Dockerfile, serving/app.py and the k8s
manifests -- no docker build, no network, no DB. Three pieces:

  COPIED    -- every root-level '<name>.py' the Dockerfile COPYs verbatim
               (parse_dockerfile_copies). 'COPY agriforecast_ml
               ./agriforecast_ml' means the whole package is already present,
               so package-internal (relative, dotted) imports never need a
               matching COPY line -- and never generate one, since
               extract_bare_root_imports only matches bare 'import X' /
               'from X import ...', which a relative import's leading dot
               can never satisfy.
  REQUIRED  -- the union of (a) every bare import in serving/app.py
               (module-level or lazy-inside-a-function -- the regex does not
               care about indentation) that resolves to a root-level script,
               (b) every '<name>.py' invoked as a python script in the k8s
               daily/monthly pipeline manifests, and (c) the transitive
               closure of (a)+(b) over each such script's OWN bare root
               imports (e.g. build_features.py -> qa_features.py).

The assertion is REQUIRED subset-of COPIED. Deliberately-excluded CLIs
(ingest_cbsl.py, ingest_harti.py, backfill_fruit_history.py,
backfill_training_log.py, resign_promoted.py) are not hardcoded as an
allowlist anywhere here -- they simply never appear in REQUIRED, because
nothing in serving/app.py or the k8s manifests references them.
"""
from __future__ import annotations

import re
from pathlib import Path
from typing import Callable, Optional

import pytest

ML_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = ML_ROOT.parents[1]  # .../src/AgriForecast.ML -> .../src -> repo root
K8S_DIR = REPO_ROOT / "k8s"
K8S_MANIFESTS = ["pipeline-daily.yaml", "pipeline-monthly.yaml"]


# ---------------------------------------------------------------------------
# Pure parsing helpers (no filesystem access baked in beyond what's passed) --
# each one is independently exercised by the teeth test below.
# ---------------------------------------------------------------------------

def parse_dockerfile_copies(text: str) -> "set[str]":
    """Every '<name>.py' the Dockerfile COPYs verbatim (e.g. 'COPY foo.py .')."""
    return set(re.findall(r"^COPY\s+(\S+)\.py\s+\.\s*$", text, re.MULTILINE))


def extract_bare_root_imports(text: str, is_root_script: Callable[[str], bool]) -> "set[str]":
    """Every bare 'import X' / 'from X import ...' in `text` where X.py is a
    root-level script, per `is_root_script`. Catches lazy imports inside
    functions (the regex only anchors on the start of the line, whatever its
    indentation) as well as module-level ones. A relative import ('from .
    import predict', 'from ..envfile import load_env_file') can never match
    either pattern -- 'from' must be followed by a bare identifier, not a
    dot -- so package-internal imports are excluded for free, with no special
    casing needed for the 'COPY agriforecast_ml ./agriforecast_ml' line.
    """
    names = set()
    names.update(re.findall(r"^\s*import\s+([A-Za-z_][A-Za-z0-9_]*)\s*$", text, re.MULTILINE))
    names.update(re.findall(r"^\s*from\s+([A-Za-z_][A-Za-z0-9_]*)\s+import\b", text, re.MULTILINE))
    return {n for n in names if is_root_script(n)}


def extract_k8s_script_invocations(text: str) -> "set[str]":
    """Every '<name>.py' invoked as a python script in one k8s manifest.

    Covers both the `command: ["python", "name.py"]` array form and the
    shell-wrapped `python name.py` / `exec python name.py` form used inside
    multi-line `args: |` blocks. Full-line comments are stripped first, so a
    script name merely mentioned in a header comment (this repo's manifests
    narrate the whole pipeline in comments, including scripts invoked two
    steps later) is never mistaken for an actual invocation.
    """
    live_lines = [ln for ln in text.splitlines() if not ln.strip().startswith("#")]
    live_text = "\n".join(live_lines)
    names = set(re.findall(r'\["python",\s*"([A-Za-z_][A-Za-z0-9_]*)\.py"\]', live_text))
    names.update(re.findall(r"\bpython\s+([A-Za-z_][A-Za-z0-9_]*)\.py\b", live_text))
    return names


def compute_required_closure(
    seed_names: "set[str]",
    is_root_script: Callable[[str], bool],
    read_script_text: Callable[[str], Optional[str]],
) -> "set[str]":
    """BFS transitive closure: a required root script also requires
    whatever OTHER root scripts IT bare-imports (e.g. build_features.py
    imports qa_features.py, so qa_features must be required too, even
    though nothing outside build_features.py imports it directly)."""
    required: "set[str]" = set()
    frontier = set(seed_names)
    while frontier:
        required |= frontier
        next_frontier: "set[str]" = set()
        for name in frontier:
            text = read_script_text(name)
            if text is None:
                continue
            next_frontier |= extract_bare_root_imports(text, is_root_script) - required
        frontier = next_frontier
    return required


# ---------------------------------------------------------------------------
# The real test: run the closure over this checkout's actual files.
# ---------------------------------------------------------------------------

def test_dockerfile_copies_cover_every_script_serving_or_k8s_can_reach():
    dockerfile_text = (ML_ROOT / "Dockerfile").read_text()
    copied = parse_dockerfile_copies(dockerfile_text)

    root_scripts = {p.stem for p in ML_ROOT.glob("*.py")}

    def is_root_script(name: str) -> bool:
        return name in root_scripts

    def read_script_text(name: str) -> Optional[str]:
        p = ML_ROOT / f"{name}.py"
        return p.read_text() if p.exists() else None

    app_py_text = (ML_ROOT / "agriforecast_ml" / "serving" / "app.py").read_text()
    seeds = extract_bare_root_imports(app_py_text, is_root_script)

    if not K8S_DIR.exists():
        pytest.skip(f"k8s manifest dir not present in this checkout ({K8S_DIR})")
    for manifest_name in K8S_MANIFESTS:
        manifest_path = K8S_DIR / manifest_name
        if manifest_path.exists():
            seeds |= extract_k8s_script_invocations(manifest_path.read_text())

    required = compute_required_closure(seeds, is_root_script, read_script_text)

    missing = required - copied
    assert not missing, (
        f"Dockerfile is missing a COPY line for: {sorted(missing)}. "
        f"Add 'COPY {sorted(missing)[0]}.py .' (one line per name) to "
        f"{ML_ROOT / 'Dockerfile'} -- without it, this script ModuleNotFoundErrors "
        "at RUNTIME (a lazy import inside serving/app.py, or a k8s pipeline step "
        "trying to exec it), never at image build or serving startup -- exactly "
        "the ingest_cbsl_service.py incident (4 nights of 503s on "
        "/admin/ingest-cbsl before anyone noticed)."
    )


# ---------------------------------------------------------------------------
# Teeth test: prove the closure logic itself actually catches a gap, on a
# synthetic in-memory fixture -- independent of the real Dockerfile/app.py/k8s
# files, so a future refactor that quietly makes the assert above vacuous
# (e.g. REQUIRED always computing empty) gets caught here instead.
# ---------------------------------------------------------------------------

def test_closure_logic_reports_a_deliberately_missing_copy():
    fake_dockerfile = """\
FROM python:3.12-slim
COPY agriforecast_ml ./agriforecast_ml
COPY build_features.py .
COPY qa_features.py .
"""
    # build_features.py bare-imports qa_features (present) AND a second
    # sibling script, sibling_helper (deliberately NOT COPY'd above).
    fake_scripts = {
        "build_features": "import sys\nimport qa_features\nimport sibling_helper\n",
        "qa_features": "import sys\n",
        "sibling_helper": "import json\n",
    }

    def is_root_script(name: str) -> bool:
        return name in fake_scripts

    def read_script_text(name: str) -> Optional[str]:
        return fake_scripts.get(name)

    copied = parse_dockerfile_copies(fake_dockerfile)
    assert copied == {"build_features", "qa_features"}

    seeds = extract_bare_root_imports(fake_scripts["build_features"], is_root_script)
    required = compute_required_closure(seeds | {"build_features"}, is_root_script, read_script_text)

    assert required == {"build_features", "qa_features", "sibling_helper"}
    missing = required - copied
    assert missing == {"sibling_helper"}, (
        "the closure checker failed to flag a deliberately-missing COPY line "
        "in the synthetic fixture -- it has lost its teeth"
    )
