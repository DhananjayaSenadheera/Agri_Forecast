"""Static (no-Docker) guard against the "missing COPY" bug class.

This has now bitten the image FOUR times -- the latest, ingest_cbsl_service.py,
made /admin/ingest-cbsl 503 every night for 4 nights, because it is only
lazily `import`'d inside the endpoint (serving/app.py), so a missing COPY
never surfaces at container startup, only at request time. There was no test
guarding this before.

Pure text/regex analysis over the Dockerfile, serving/app.py and the k8s
manifests -- no docker build, no network, no DB. Three pieces:

  COPIED    -- every root-level '<name>.py' the Dockerfile COPYs verbatim
               (parse_dockerfile_copies). A commented-out COPY line never
               counts. 'COPY agriforecast_ml ./agriforecast_ml' means the
               whole package is already present, so package-internal
               (relative, dotted) imports never need a matching COPY line --
               and never generate one, since extract_bare_root_imports only
               matches bare 'import X' / 'from X import ...', which a
               relative import's leading dot can never satisfy.
  REQUIRED  -- the union of (a) every bare import in serving/app.py
               (module-level or lazy-inside-a-function, at any indentation)
               that resolves to a root-level script, (b) every '<name>.py'
               invoked as a python script in every k8s/*.yaml manifest (glob,
               not a hardcoded filename pair -- see
               extract_k8s_script_invocations), and (c) the transitive
               closure of (a)+(b) over each such script's OWN bare root
               imports (e.g. build_features.py -> qa_features.py).

The assertion is REQUIRED subset-of COPIED. Deliberately-excluded CLIs
(ingest_cbsl.py, ingest_harti.py, backfill_fruit_history.py,
backfill_training_log.py, resign_promoted.py) are not hardcoded as an
allowlist anywhere here -- they simply never appear in REQUIRED, because
nothing in serving/app.py or the k8s manifests references them.

ROUND-2 REVIEW (mutation testing) NOTE: an earlier version of this file
claimed the teeth test below "catches a future refactor that quietly makes
[the real test's] assert vacuous". That was false -- the teeth test runs the
closure functions against its OWN synthetic fixture through its own call
path; it shares no state with the real test and cannot detect e.g. someone
replacing `required = compute_required_closure(...)` with `required = set()`
inside the real test, or the import regex silently being re-anchored to
column 0. What the teeth test actually guarantees: the parse/extract/closure
FUNCTIONS themselves correctly detect a missing COPY, correctly respect BFS
transitivity, tolerate indented lazy imports, and correctly ignore comments,
all independently exercised on one synthetic case. Protection for the real
test's OWN call sites against being gutted comes from the positive
non-vacuity assertion inside the real test itself
(`required >= {...known must-have names...}`), not from the teeth test --
these two tests are complementary, not redundant.
"""
from __future__ import annotations

import re
from pathlib import Path
from typing import Callable, Optional

ML_ROOT = Path(__file__).resolve().parents[1]
REPO_ROOT = ML_ROOT.parents[1]  # .../src/AgriForecast.ML -> .../src -> repo root
K8S_DIR = REPO_ROOT / "k8s"


# ---------------------------------------------------------------------------
# Pure parsing helpers (no filesystem access baked in beyond what's passed) --
# each one is independently exercised by the teeth test below.
# ---------------------------------------------------------------------------

def parse_dockerfile_copies(text: str) -> "set[str]":
    """Every '<name>.py' the Dockerfile COPYs verbatim (e.g. 'COPY foo.py .').

    A commented-out line ('# COPY foo.py .') must NOT count -- full-line
    comments are stripped before the COPY regex ever sees the text.
    """
    live_lines = [ln for ln in text.splitlines() if not ln.strip().startswith("#")]
    live_text = "\n".join(live_lines)
    return set(re.findall(r"^COPY\s+(\S+)\.py\s+\.\s*$", live_text, re.MULTILINE))


_IMPORT_LINE_RE = re.compile(r"^[ \t]*import[ \t]+([^\n]+)$", re.MULTILINE)
_FROM_IMPORT_RE = re.compile(r"^[ \t]*from[ \t]+([A-Za-z_][A-Za-z0-9_]*)[ \t]+import\b", re.MULTILINE)
_AS_SPLIT_RE = re.compile(r"\s+as\s+")


def extract_bare_root_imports(text: str, is_root_script: Callable[[str], bool]) -> "set[str]":
    """Every bare 'import X' / 'from X import ...' in `text` where X.py is a
    root-level script, per `is_root_script`.

    Handles, deliberately (each of these was a live blind spot found by
    round-1 mutation testing):
      - lazy imports inside a function, at ANY indentation (the regex
        anchors only on '^[ \\t]*', never column 0);
      - 'import x as y' aliasing;
      - comma-separated 'import x, y';
      - a trailing '# noqa'-style inline comment on the import line.

    A relative import ('from . import predict', 'from ..envfile import
    load_env_file') can never match either pattern -- 'from' must be
    followed by a bare identifier, not a dot -- so package-internal imports
    are excluded for free, with no special casing needed for the 'COPY
    agriforecast_ml ./agriforecast_ml' line.
    """
    names = set()
    for m in _IMPORT_LINE_RE.finditer(text):
        body = m.group(1).split("#", 1)[0].strip()  # drop a trailing inline comment
        if not body:
            continue
        for clause in body.split(","):
            module = _AS_SPLIT_RE.split(clause.strip(), maxsplit=1)[0].strip()
            if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", module):
                names.add(module)
    names.update(m.group(1) for m in _FROM_IMPORT_RE.finditer(text))
    return {n for n in names if is_root_script(n)}


# Array form: ["python", "name.py"] / ["python3", "-u", "name.py"] / etc --
# any number of other quoted args (flags) between the interpreter and the
# final '<name>.py' argument, which must be the last element before ']'.
_K8S_ARRAY_RE = re.compile(
    r'\[\s*"python3?"(?:\s*,\s*"[^"]*")*?\s*,\s*"([A-Za-z_][A-Za-z0-9_]*)\.py"\s*\]'
)
# Shell form: 'python name.py' / 'exec python name.py' / 'python3 -u name.py'
# -- optional short flags (-u, -X dev, ...) between the interpreter and the
# script name.
_K8S_SHELL_RE = re.compile(r"\bpython3?\b(?:\s+-\S+)*\s+([A-Za-z_][A-Za-z0-9_]*)\.py\b")


def extract_k8s_script_invocations(text: str) -> "set[str]":
    """Every '<name>.py' invoked as a python script in one k8s manifest.

    Covers both the `command: ["python", "name.py"]` array form (including
    'python3' and interstitial flag args like '-u') and the shell-wrapped
    `python name.py` / `exec python name.py` form used inside multi-line
    `args: |` blocks. Full-line comments are stripped first, so a script
    name merely mentioned in a header comment (this repo's manifests narrate
    the whole pipeline in comments, including scripts invoked steps later)
    is never mistaken for an actual invocation.
    """
    live_lines = [ln for ln in text.splitlines() if not ln.strip().startswith("#")]
    live_text = "\n".join(live_lines)
    names = set(_K8S_ARRAY_RE.findall(live_text))
    names.update(_K8S_SHELL_RE.findall(live_text))
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
    dockerfile_text = (ML_ROOT / "Dockerfile").read_text(encoding="utf-8")
    copied = parse_dockerfile_copies(dockerfile_text)

    root_scripts = {p.stem for p in ML_ROOT.glob("*.py")}

    def is_root_script(name: str) -> bool:
        return name in root_scripts

    def read_script_text(name: str) -> Optional[str]:
        p = ML_ROOT / f"{name}.py"
        return p.read_text(encoding="utf-8") if p.exists() else None

    # (a) serving/app.py leg -- runs UNCONDITIONALLY, independent of k8s.
    # This is the leg that actually caught the real ingest_cbsl_service
    # outage (a lazy import inside an admin route), and it needs no k8s
    # manifests at all -- an absent/renamed k8s dir must never skip it.
    app_py_text = (ML_ROOT / "agriforecast_ml" / "serving" / "app.py").read_text(encoding="utf-8")
    seeds = extract_bare_root_imports(app_py_text, is_root_script)

    # (b) k8s leg -- ADDITIVE only. If k8s/ is present, every *.yaml manifest
    # in it is globbed (not a hardcoded filename pair, so renaming
    # pipeline-daily.yaml can never silently drop its seeds); if the dir
    # exists but the glob finds nothing, that is itself a hard failure, not
    # a silent no-op.
    k8s_present = K8S_DIR.exists()
    if k8s_present:
        manifest_paths = sorted(K8S_DIR.glob("*.yaml"))
        assert manifest_paths, f"{K8S_DIR} exists but contains no *.yaml manifests"
        for manifest_path in manifest_paths:
            seeds |= extract_k8s_script_invocations(manifest_path.read_text(encoding="utf-8"))

    required = compute_required_closure(seeds, is_root_script, read_script_text)

    # Positive non-vacuity pin, independent of the subset assert below.
    # Kills any mutation that guts the real call path (e.g.
    # `required = set()`, or the import regex silently re-anchored to
    # column 0 so app.py's four indented lazy imports stop matching):
    # ingest_cbsl_service only reaches REQUIRED via a real lazy (indented)
    # import in serving/app.py; qa_features and build_features only reach it
    # via BFS transitivity through the k8s leg (build_features.py is a k8s
    # entry point; qa_features is reachable only because build_features.py
    # itself imports it) -- so this one assertion pins both legs AND
    # transitivity AND non-emptiness at once, against the REAL files.
    if k8s_present:
        assert required >= {"ingest_cbsl_service", "qa_features", "build_features"}
    else:
        assert required >= {"ingest_cbsl_service"}

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
# Teeth test: prove the closure FUNCTIONS themselves actually catch a gap, on
# a synthetic in-memory fixture -- independent of the real Dockerfile/app.py/
# k8s files. See the module docstring's ROUND-2 REVIEW note for exactly what
# this test does and does not guarantee.
# ---------------------------------------------------------------------------

def test_closure_logic_reports_a_deliberately_missing_copy():
    fake_dockerfile = """\
FROM python:3.12-slim
COPY agriforecast_ml ./agriforecast_ml
COPY build_features.py .
COPY qa_features.py .
# COPY sibling_helper.py .
"""
    # build_features.py bare-imports qa_features at module level (exercises
    # plain, non-indented BFS transitivity) AND sibling_helper only via a
    # LAZY, INDENTED import inside a nested function (exercises that the
    # extraction regex is indentation-tolerant, not anchored to column 0).
    # sibling_helper is deliberately NOT COPY'd -- only commented-out above
    # (a commented-out COPY line must never count as copied).
    fake_scripts = {
        "build_features": (
            "import sys\n"
            "import qa_features\n"
            "\n"
            "def _run_qa_gate():\n"
            "    import sibling_helper\n"
            "    return sibling_helper.run()\n"
        ),
        "qa_features": "import sys\n",
        "sibling_helper": "import json\n",
    }

    def is_root_script(name: str) -> bool:
        return name in fake_scripts

    def read_script_text(name: str) -> Optional[str]:
        return fake_scripts.get(name)

    copied = parse_dockerfile_copies(fake_dockerfile)
    assert copied == {"build_features", "qa_features"}  # the commented line never counts

    # Seed with ONLY the k8s-reachable entry point, "build_features" -- NOT
    # with extract_bare_root_imports(fake_scripts["build_features"], ...),
    # which would put "sibling_helper" directly into the seed set and let a
    # compute_required_closure degenerate to `return set(seed_names)` (BFS
    # transitivity deleted) still pass. Here, "qa_features" AND
    # "sibling_helper" are reachable ONLY through the BFS step over
    # build_features.py's own text -- and "sibling_helper" specifically only
    # via its indented lazy-import line.
    seeds = {"build_features"}
    required = compute_required_closure(seeds, is_root_script, read_script_text)

    assert required == {"build_features", "qa_features", "sibling_helper"}
    missing = required - copied
    assert missing == {"sibling_helper"}, (
        "the closure checker failed to flag a deliberately-missing COPY line "
        "in the synthetic fixture -- it has lost its teeth"
    )
