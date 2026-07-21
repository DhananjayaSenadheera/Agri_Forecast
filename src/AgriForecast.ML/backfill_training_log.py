"""CLI: one-off, idempotent backfill of the ModelTrainingRuns table (admin Logs
hub Phase 2, PR B) from the on-disk file registry.

Reads every ``models/<version>/metadata.json`` plus ``models/promoted.json`` and
upserts one ModelTrainingRuns row per version, then re-syncs the Promoted flags.
Existing rows created by the live save_model hook are UPDATED in place, so this
is safe to re-run at any time (idempotent).

Truth model (matches training_log / the frozen table contract):
  * DecisionPromoted  <- each version's metadata ``promoted`` field. That IS the
                         automated gate's verdict recorded at train time.
  * Promoted          <- 1 ONLY for the version promoted.json currently points
                         at, 0 for all others. Sourced from the pointer AFTER
                         all rows are upserted -- never from metadata.
  These legitimately DIVERGE on a manual override: v17's metadata says
  promoted=false (DecisionPromoted=0) yet it is the live pointer (Promoted=1).

Usage:
    ./.venv/bin/python backfill_training_log.py [--models-dir PATH] [--dry-run]

  --models-dir  Override the registry models directory (default: the registry's
                own ``_MODELS_DIR``). Used by tests against synthetic fixtures.
  --dry-run     Read + print the per-version summary + totals, write NOTHING.

Exit codes:
  0  backfill completed (or dry-run printed) successfully.
  2  infrastructure error -- could not read the registry or could not write to
     the DB. Never leaks credentials / paths / tracebacks to stdout; redacted
     type+message goes to stderr via logging only.
"""
from __future__ import annotations

import argparse
import json
import logging
import sys
from pathlib import Path

from agriforecast_ml import training_log
from agriforecast_ml.envfile import load_env_file
from agriforecast_ml.db import get_engine
from agriforecast_ml.registry import registry

logger = logging.getLogger(__name__)


def _log_exception(prefix: str, exc: BaseException) -> None:
    """logging.error with the exception TYPE intact and its MESSAGE redacted --
    mirrors verify_ingestion._log_exception. No traceback (house rules)."""
    logging.error("%s: %s: %s", prefix, type(exc).__name__,
                  training_log.redact_sensitive(str(exc)))


def _version_sort_key(name: str) -> tuple:
    """Sort v<N> numerically (v2 before v10); anything else sorts last by name."""
    if name.startswith("v") and name[1:].isdigit():
        return (0, int(name[1:]))
    return (1, name)


def _read_promoted_version(models_dir: Path) -> "str | None":
    pointer = models_dir / "promoted.json"
    if not pointer.exists():
        return None
    try:
        return json.loads(pointer.read_text()).get("version")
    except (ValueError, OSError):
        return None


def collect_versions(models_dir: Path) -> "tuple[list[dict], str | None]":
    """Read every version dir's metadata.json and return
    ``(rows, promoted_version)`` where ``rows`` is a list of per-version value
    dicts (from training_log.values_from_metadata, each carrying a resolved
    ``_promoted`` flag). Sorted numerically by version. Raises OSError/
    ValueError only on a genuinely unreadable registry (caught by the CLI)."""
    if not models_dir.exists():
        raise FileNotFoundError(f"models dir does not exist: {models_dir}")

    promoted_version = _read_promoted_version(models_dir)

    rows: list[dict] = []
    version_dirs = [
        p for p in models_dir.iterdir()
        if p.is_dir() and p.name.startswith("v") and p.name[1:].isdigit()
    ]
    for vdir in sorted(version_dirs, key=lambda p: _version_sort_key(p.name)):
        meta_path = vdir / "metadata.json"
        if not meta_path.exists():
            logger.warning("skipping %s: no metadata.json", vdir.name)
            continue
        try:
            metadata = json.loads(meta_path.read_text())
        except (ValueError, OSError) as exc:
            logger.warning("skipping %s: unreadable metadata.json (%s)",
                           vdir.name, type(exc).__name__)
            continue
        # metadata.json may predate the "version" field being written into it;
        # fall back to the directory name so the row is still keyed correctly.
        metadata.setdefault("version", vdir.name)
        vals = training_log.values_from_metadata(metadata)
        vals["_promoted"] = 1 if vals["version"] == promoted_version else 0
        rows.append(vals)

    return rows, promoted_version


def _print_summary(rows: "list[dict]", promoted_version: "str | None", dry_run: bool) -> None:
    header = "=== ModelTrainingRuns backfill" + (" (DRY-RUN)" if dry_run else "") + " ==="
    print(header)
    print(f"  models pointer -> {promoted_version or '(none)'}")
    print(f"  {'VERSION':<8} {'PROMOTED':<9} {'DECISION':<9} {'BEST_ML':<14} "
          f"{'ML_MAE':<9} {'BEST_BASE':<14} {'BASE_MAE':<9}")
    n_promoted = 0
    n_decision = 0
    for r in rows:
        promoted = r["_promoted"]
        decision = 1 if r["decision_promoted"] else 0
        n_promoted += promoted
        n_decision += decision
        ml_mae = r["best_ml_mae"]
        base_mae = r["best_baseline_mae"]
        print(f"  {str(r['version']):<8} {promoted:<9} {decision:<9} "
              f"{str(r['best_ml_kind'] or '-'):<14} "
              f"{('-' if ml_mae is None else round(float(ml_mae), 2)):<9} "
              f"{str(r['best_baseline_kind'] or '-'):<14} "
              f"{('-' if base_mae is None else round(float(base_mae), 2)):<9}")
    print(f"  totals: {len(rows)} version(s), {n_promoted} promoted, "
          f"{n_decision} gate-promoted (DecisionPromoted=1)")
    if n_promoted > 1:
        # Should be impossible (single pointer) -- surface loudly if the registry
        # is inconsistent rather than silently writing two live rows.
        print("  WARNING: more than one version resolved to Promoted=1 -- "
              "promoted.json pointer is ambiguous.")


def _write(engine, rows: "list[dict]", promoted_version: "str | None") -> None:
    """Upsert every version row, then sync Promoted flags from the pointer."""
    for r in rows:
        kwargs = {k: v for k, v in r.items() if not k.startswith("_")}
        training_log.upsert_training_run(engine, **kwargs)
    # Re-affirm the live pointer AFTER all rows exist. If nothing is promoted,
    # sync to a sentinel that matches no row -> every row Promoted=0 (truthful).
    training_log.sync_promoted_flags(engine, promoted_version or "\x00__none__")


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--models-dir", type=str, default=None,
                    help="Override the registry models directory (default: the "
                         "registry's own _MODELS_DIR).")
    ap.add_argument("--dry-run", action="store_true",
                    help="Print the summary but write nothing.")
    args = ap.parse_args()
    logging.basicConfig(level=logging.WARNING, format="%(levelname)s %(message)s")

    models_dir = Path(args.models_dir) if args.models_dir else registry._MODELS_DIR

    try:
        rows, promoted_version = collect_versions(models_dir)
    except Exception as exc:  # noqa: BLE001 -- CLI boundary
        print(f"ERROR: could not read the model registry "
              f"({type(exc).__name__}). See logs for detail.")
        _log_exception("backfill_training_log: registry read error", exc)
        return 2

    _print_summary(rows, promoted_version, args.dry_run)

    if args.dry_run:
        print("  (--dry-run: nothing written)")
        return 0

    if not rows:
        print("  no versions to backfill; nothing written.")
        return 0

    try:
        load_env_file()
        engine = get_engine()
        _write(engine, rows, promoted_version)
    except Exception as exc:  # noqa: BLE001 -- CLI boundary
        print(f"ERROR: backfill could not write to the database "
              f"({type(exc).__name__}). See logs for detail.")
        _log_exception("backfill_training_log: DB write error", exc)
        return 2

    print(f"  wrote {len(rows)} version(s); live pointer = {promoted_version or '(none)'}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
