"""CLI: run the post-ingestion 14-check data-quality verification battery and
persist one verdict row to IngestionVerifications (R2 ingestion-log plan
PR 2). See agriforecast_ml/ingest_verify.py for the full check battery and
design rationale.

Daily wiring (run-daily.sh, AFTER mirror_dec_prices.py, BEFORE
build_features.py -- see that script; NOT modified by this change, it is
machine-local/gitignored):
  ./.venv/bin/python verify_ingestion.py --batch-id "$BATCH_ID"

Manual re-check of a past date:
  ./.venv/bin/python verify_ingestion.py --pipeline-date 2026-07-20 --dry-run

Exit codes:
  0  overall PASS or WARN (verdict persisted unless --dry-run)
  1  overall FAIL (a completed run that found a FAIL-severity check;
     verdict IS still persisted so the FAIL is visible in the log)
  2  infrastructure error -- verification could not run at all, OR it ran
     but the verdict row could not be persisted (distinct from a
     verification FAIL: this is "we don't know", not "we checked and it's
     bad"). Never leaks credentials/paths/tracebacks to stdout; full
     exception detail (type + message, no traceback) goes to stderr via
     logging only.
"""
from __future__ import annotations

import argparse
import logging
import re
import sys
import uuid
from datetime import date

from agriforecast_ml.envfile import load_env_file
from agriforecast_ml.db import get_engine
from agriforecast_ml import ingest_verify

# --------------------------------------------------------------------------
# S2 review fix: exception __str__ (e.g. from a SQLAlchemy/pymssql
# connectivity failure) can embed the full DSN, including the password, or
# a local filesystem path -- this project's house rule is "never log/print
# .env values or connection strings" and "errors never leak ... paths",
# with NO exception carved out for "but it's just going to a log file".
# Every exception message this CLI logs (the only place raw exception text
# is ever emitted -- stdout print()s below never include str(exc) at all)
# is passed through this best-effort redaction first. Exception TYPE names
# are never redacted (they carry no secrets and are useful for triage).
# --------------------------------------------------------------------------
_CONN_STRING_KEYS = (
    "password", "pwd", "server", "uid", "user id", "database",
    "data source", "initial catalog", "app id", "app secret",
)
_CONN_STRING_RE = re.compile(
    r"(?i)\b(" + "|".join(re.escape(k) for k in _CONN_STRING_KEYS) + r")\s*=\s*[^;]*"
)
# Windows drive paths (C:\...) or unix/UNC absolute paths (/... or \\...).
_PATH_RE = re.compile(r"(?:[A-Za-z]:\\[^\s\"';]*|\\\\[^\s\"';]*|/[^\s\"';]*)")


def _redact_sensitive(text: str) -> str:
    """Best-effort redaction of connection-string fragments (key=value
    pairs for known secret-bearing keys) and filesystem paths from an
    exception message, before it is ever passed to logging.error(). Applied
    unconditionally -- not just to exceptions we expect to be "safe" -- since
    the whole point is defending against a connectivity failure whose
    message happens to embed the DSN we never intended to log."""
    text = _CONN_STRING_RE.sub(lambda m: f"{m.group(1)}=<redacted>", text)
    text = _PATH_RE.sub("<path-redacted>", text)
    return text


def _log_exception(prefix: str, exc: BaseException) -> None:
    """logging.error() with the exception TYPE intact but its MESSAGE
    redacted -- see _redact_sensitive. No traceback (exc_info is never
    passed) -- stdout/stderr must never carry one, per house rules."""
    logging.error("%s: %s: %s", prefix, type(exc).__name__, _redact_sensitive(str(exc)))


def _print_report(result: dict) -> None:
    print(f"=== Ingestion verification ({result['pipeline_date']}) ===")
    print(
        f"  overall: {result['overall']}  "
        f"(pass={result['n_pass']} warn={result['n_warn']} fail={result['n_fail']}, "
        f"{result['duration_ms']}ms)"
    )
    print(f"  {'CHECK':<22} {'SEVERITY':<7} MESSAGE")
    for c in result["checks"]:
        print(f"  {c['name']:<22} {c['severity']:<7} {c['message']}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--pipeline-date", type=str, default=None,
                     help="ISO YYYY-MM-DD; the date being verified (default: today in UTC, "
                          "not the runner's local date -- see ingest_verify.run_all_verifications)")
    ap.add_argument("--batch-id", type=str, default=None,
                     help="Optional GUID linking this verdict to an IngestionRuns batch")
    ap.add_argument("--dry-run", action="store_true",
                     help="Run checks and print the report, but skip persistence")
    args = ap.parse_args()
    # WARNING, not INFO: data_quality.gap_report() (check 14) logs one INFO
    # line per gap entry across the WHOLE HARTI corpus (hundreds/thousands),
    # which would otherwise flood this CLI's stdout/stderr on every run. This
    # module's own report (_print_report) is the primary human-facing output
    # regardless of log level.
    logging.basicConfig(level=logging.WARNING, format="%(levelname)s %(message)s")
    # gap_report() emits its per-gap lines at WARNING/ERROR level, so the
    # basicConfig threshold above does not stop them (1.8MB observed on a
    # full-corpus run). Check 14 consumes gap_report's structured return
    # value; the log stream is redundant here, so silence that logger.
    logging.getLogger("agriforecast_ml.data_quality").setLevel(logging.CRITICAL)

    try:
        pipeline_date = date.fromisoformat(args.pipeline_date) if args.pipeline_date else None
        batch_id = uuid.UUID(args.batch_id) if args.batch_id else None
    except ValueError as exc:
        print(f"ERROR: invalid argument ({type(exc).__name__}).")
        _log_exception("verify_ingestion: invalid CLI argument", exc)
        return 2

    try:
        load_env_file()
        engine = get_engine()
        result = ingest_verify.run_all_verifications(
            engine, pipeline_date=pipeline_date, batch_id=batch_id,
        )
    except Exception as exc:  # noqa: BLE001 -- top-level CLI boundary, see module docstring
        print(f"ERROR: verification could not run due to an infrastructure error "
              f"({type(exc).__name__}). See logs for detail.")
        _log_exception("verify_ingestion: infrastructure error", exc)
        return 2

    _print_report(result)

    if not args.dry_run:
        try:
            verdict_id = ingest_verify.persist_verdict(engine, result, batch_id=batch_id)
            print(f"  persisted verdict: {verdict_id}")
        except Exception as exc:  # noqa: BLE001
            print(f"ERROR: verification ran but the verdict row could not be "
                  f"persisted ({type(exc).__name__}). See logs for detail.")
            _log_exception("verify_ingestion: failed to persist verdict", exc)
            return 2
    else:
        print("  (--dry-run: verdict not persisted)")

    return 1 if result["overall"] == "FAIL" else 0


if __name__ == "__main__":
    sys.exit(main())
