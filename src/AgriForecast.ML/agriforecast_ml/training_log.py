"""Model-training run log: every training run writes one ModelTrainingRuns row.

The table schema is owned by the .NET side; this module is the sanctioned Python writer
and the column contract is FROZEN - do not add or rename columns here.

upsert_training_run UPSERTs one row keyed on the unique Version: UPDATE first, INSERT
only if nothing matched, so a retrain that re-emits a version cannot trip the unique
constraint. It deliberately does not touch Promoted.

sync_promoted_flags owns Promoted: 1 for the live version, 0 for every other row. The
live version is read from promoted.json AFTER the promotion decision, never echoed from a
version's own metadata - a manual override can make the live pointer and the gate's
recorded verdict legitimately disagree, and echoing metadata would mislabel the live
model.

MAE values are rounded to fit decimal(10,2), text fields are truncated to their column
caps and every field is None-safe. Never log a connection string or .env value; error
text is redacted before any warning. Both primitives RAISE on a genuine DB error - the
fail-open posture lives in the caller (registry.save_model).
"""
from __future__ import annotations

import logging
import re
from datetime import datetime, timezone

import sqlalchemy as sa

logger = logging.getLogger(__name__)

# Column caps for the nvarchar fields. Truncate defensively: losing a few trailing chars
# of a reason string beats failing a best-effort log write on a length error.
_VERSION_CAP = 20
_PROMOTION_DECISION_CAP = 2000
_KIND_CAP = 50
_CONTRACT_HASH_CAP = 100

# Redaction (minimal replica of verify_ingestion._redact_sensitive -- that
# lives in a root CLI script and is awkward to import from inside the package;
# kept intentionally small and dependency-free here so registry.save_model can
# reuse it for its fail-open warning).
_CONN_STRING_KEYS = (
    "password", "pwd", "server", "uid", "user id", "database",
    "data source", "initial catalog", "app id", "app secret",
)
_CONN_STRING_RE = re.compile(
    r"(?i)\b(" + "|".join(re.escape(k) for k in _CONN_STRING_KEYS) + r")\s*=\s*[^;]*"
)
_PATH_RE = re.compile(r"(?:[A-Za-z]:\\[^\s\"';]*|\\\\[^\s\"';]*|/[^\s\"';]*)")


def redact_sensitive(text: str) -> str:
    """Best-effort redaction of connection-string fragments and filesystem
    paths from a message before it is logged -- see verify_ingestion for the
    full rationale. Applied unconditionally."""
    text = _CONN_STRING_RE.sub(lambda m: f"{m.group(1)}=<redacted>", str(text))
    text = _PATH_RE.sub("<path-redacted>", text)
    return text


# Value coercion helpers (all None-safe)

def _truncate(val: object, cap: int) -> object:
    """Truncate a string to ``cap`` chars; pass through None / non-str."""
    if isinstance(val, str):
        return val[:cap]
    return val


def _round2(val: object) -> "float | None":
    """Round a numeric to 2dp to fit decimal(10,2); None-safe. A non-numeric
    value returns None rather than raising (best-effort log row)."""
    if val is None:
        return None
    try:
        return round(float(val), 2)
    except (TypeError, ValueError):
        return None


def _to_bit(val: object) -> "int | None":
    """Coerce a truthy/None value to a SQL bit (0/1); None stays None."""
    if val is None:
        return None
    return 1 if bool(val) else 0


def _to_int(val: object) -> "int | None":
    if val is None:
        return None
    try:
        return int(val)
    except (TypeError, ValueError):
        return None


def _to_datetime(val: object) -> object:
    """Accept a datetime or an ISO-8601 string and return a NAIVE-UTC datetime.

    SQL Server datetime2 has no offset, and a tz-aware bind would silently no-op in
    production rather than raise, so tz-aware values are converted to UTC then stripped.
    Non-parseable or None values pass through unchanged.
    """
    if isinstance(val, str):
        try:
            val = datetime.fromisoformat(val)
        except ValueError:
            return val
    if isinstance(val, datetime) and val.tzinfo is not None:
        return val.astimezone(timezone.utc).replace(tzinfo=None)
    return val


def values_from_metadata(metadata: dict) -> dict:
    """Extract the ModelTrainingRuns field values from the metadata dict save_model builds.

    Shared by the save_model hook and the backfill script so train-time and backfill rows are
    populated identically. decision_promoted comes from metadata['promoted'], the gate's
    verdict at train time; Promoted is set separately by sync_promoted_flags.
    """
    cv = metadata.get("cv") or {}
    return {
        "version": metadata.get("version"),
        "trained_at": metadata.get("trained_at"),
        "decision_promoted": metadata.get("promoted"),
        "promotion_decision": metadata.get("promotion_decision"),
        # Older metadata used flat "model_MAE"/"cropmean_MAE"; prefer the
        # multi-candidate gate keys, fall back for pre-gate versions.
        "best_ml_kind": cv.get("best_ml", cv.get("best_ml_kind")),
        "best_ml_mae": cv.get("best_ml_MAE", cv.get("model_MAE")),
        "best_baseline_kind": cv.get("best_baseline", cv.get("best_baseline_kind")),
        "best_baseline_mae": cv.get("best_baseline_MAE", cv.get("cropmean_MAE")),
        "n_train_rows": metadata.get("n_train_rows"),
        "n_crops": metadata.get("n_crops"),
        "feature_contract_hash": metadata.get("feature_contract_hash"),
    }


# Write primitives

_UPDATE_SQL = sa.text(
    """UPDATE ModelTrainingRuns
          SET TrainedAtUtc        = :trained_at,
              DecisionPromoted    = :decision_promoted,
              PromotionDecision   = :promotion_decision,
              BestMlKind          = :best_ml_kind,
              BestMlMae           = :best_ml_mae,
              BestBaselineKind    = :best_baseline_kind,
              BestBaselineMae     = :best_baseline_mae,
              NTrainRows          = :n_train_rows,
              NCrops              = :n_crops,
              FeatureContractHash = :feature_contract_hash
        WHERE Version = :version"""
)

# INSERT seeds Promoted=0; sync_promoted_flags corrects the live pointer to 1
# right after. Id is identity; CreatedUtc is a DB default -- neither is set here.
_INSERT_SQL = sa.text(
    """INSERT INTO ModelTrainingRuns
         (Version, TrainedAtUtc, Promoted, DecisionPromoted, PromotionDecision,
          BestMlKind, BestMlMae, BestBaselineKind, BestBaselineMae,
          NTrainRows, NCrops, FeatureContractHash)
       VALUES
         (:version, :trained_at, 0, :decision_promoted, :promotion_decision,
          :best_ml_kind, :best_ml_mae, :best_baseline_kind, :best_baseline_mae,
          :n_train_rows, :n_crops, :feature_contract_hash)"""
)


def upsert_training_run(
    engine,
    *,
    version: str,
    trained_at: object,
    decision_promoted: object,
    promotion_decision: object,
    best_ml_kind: object,
    best_ml_mae: object,
    best_baseline_kind: object,
    best_baseline_mae: object,
    n_train_rows: object,
    n_crops: object,
    feature_contract_hash: object,
) -> str:
    """UPSERT one ModelTrainingRuns row keyed on the unique version.

    UPDATE first, INSERT only if nothing matched, so a retrain re-emitting a version is
    idempotent. Promoted is left untouched - sync_promoted_flags owns it.

    Raises on a genuine DB error; the caller decides whether to fail open.
    """
    params = {
        "version": _truncate(version, _VERSION_CAP),
        "trained_at": _to_datetime(trained_at),
        "decision_promoted": _to_bit(decision_promoted),
        "promotion_decision": _truncate(promotion_decision, _PROMOTION_DECISION_CAP),
        "best_ml_kind": _truncate(best_ml_kind, _KIND_CAP),
        "best_ml_mae": _round2(best_ml_mae),
        "best_baseline_kind": _truncate(best_baseline_kind, _KIND_CAP),
        "best_baseline_mae": _round2(best_baseline_mae),
        "n_train_rows": _to_int(n_train_rows),
        "n_crops": _to_int(n_crops),
        "feature_contract_hash": _truncate(feature_contract_hash, _CONTRACT_HASH_CAP),
    }
    with engine.begin() as conn:
        res = conn.execute(_UPDATE_SQL, params)
        if (res.rowcount or 0) == 0:
            conn.execute(_INSERT_SQL, params)
    logger.info("training_log.upsert_training_run: upserted version=%s", params["version"])
    return str(params["version"])


_SYNC_CLEAR_SQL = sa.text(
    "UPDATE ModelTrainingRuns SET Promoted = 0 WHERE Version <> :live_version"
)
_SYNC_SET_SQL = sa.text(
    "UPDATE ModelTrainingRuns SET Promoted = 1 WHERE Version = :live_version"
)


def sync_promoted_flags(engine, live_version: str) -> None:
    """Make Promoted truthful: 1 for live_version's row, 0 for every other row.

    Idempotent, two statements. live_version is the pointer read from promoted.json after the
    promotion decision, never a metadata field. If no row matches it yet, every row goes to 0.

    Raises on a genuine DB error; the caller decides whether to fail open.
    """
    lv = _truncate(live_version, _VERSION_CAP)
    with engine.begin() as conn:
        conn.execute(_SYNC_CLEAR_SQL, {"live_version": lv})
        conn.execute(_SYNC_SET_SQL, {"live_version": lv})
    logger.info("training_log.sync_promoted_flags: live_version=%s", lv)
