"""Model-training run log (admin Logs hub Phase 2, PR B).

Every model training run writes exactly one row to the ``ModelTrainingRuns``
SQL table (schema OWNED by the .NET side / PR A -- this module is the
sanctioned Python writer, mirroring ingest_verify.persist_verdict's posture for
IngestionVerifications). The .NET admin API reads this table; the contract is
FROZEN (see the column list below) -- do not add/rename columns here.

Two write primitives, both parameterized SQL over a SQLAlchemy engine (no ORM),
matching db.py / ingest_verify.py:

* ``upsert_training_run`` -- UPSERT one row keyed on the UNIQUE ``Version``.
  Retrains that re-emit an existing version MUST NOT crash on the unique
  constraint, so this is UPDATE-then-INSERT (rowcount-gated), never a blind
  INSERT. It deliberately does NOT touch ``Promoted`` -- that flag is owned by
  ``sync_promoted_flags`` (see below), so a retrain re-emitting a row never
  disturbs the live pointer's truth.

* ``sync_promoted_flags`` -- set ``Promoted=1`` for the current live version and
  ``Promoted=0`` for every other row, idempotently. The live version is sourced
  from ``promoted.json`` AFTER the promotion decision has completed (by the
  caller), NEVER echoed from a version's own metadata ``promoted`` field: v17 is
  live via a MANUAL override yet its metadata says ``promoted=false``, so
  ``Promoted`` (the live pointer) and ``DecisionPromoted`` (the gate's verdict
  at train time) legitimately diverge. Echoing metadata would silently mislabel
  the live model.

Column contract (ModelTrainingRuns):
  Id int identity; Version nvarchar(20) UNIQUE; TrainedAtUtc datetime2;
  Promoted bit; DecisionPromoted bit; PromotionDecision nvarchar(2000) null;
  BestMlKind nvarchar(50); BestMlMae decimal(10,2); BestBaselineKind nvarchar(50);
  BestBaselineMae decimal(10,2); NTrainRows int; NCrops int;
  FeatureContractHash nvarchar(100); CreatedUtc datetime2 (DB-default -- never
  set here).

Values are sourced from the same metadata dict ``registry.save_model`` builds
(see ``values_from_metadata``); MAE values are rounded to 2dp to fit
decimal(10,2), text fields are truncated to their column caps, and every field
is None-safe.

House rules honoured: never log a connection string / .env value; error text is
redacted before any warning (see ``redact_sensitive``); no tracebacks/paths to
stdout. These two primitives RAISE on a genuine DB error (they are unit-tested
for correctness); the FAIL-OPEN posture -- a DB failure must never break
training/saving -- lives in the caller (registry.save_model), exactly as
ingest_verify.persist_verdict raises and its CLI catches.
"""
from __future__ import annotations

import logging
import re
from datetime import datetime, timezone

import sqlalchemy as sa

logger = logging.getLogger(__name__)

# Column caps for nvarchar fields -- truncate defensively so a too-long value
# never trips a DB length error (the log row is best-effort; losing a few
# trailing chars of a reason string is preferable to failing the write).
_VERSION_CAP = 20
_PROMOTION_DECISION_CAP = 2000
_KIND_CAP = 50
_CONTRACT_HASH_CAP = 100

# ---------------------------------------------------------------------------
# Redaction (minimal replica of verify_ingestion._redact_sensitive -- that
# lives in a root CLI script and is awkward to import from inside the package;
# kept intentionally small and dependency-free here so registry.save_model can
# reuse it for its fail-open warning).
# ---------------------------------------------------------------------------
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


# ---------------------------------------------------------------------------
# Value coercion helpers (all None-safe)
# ---------------------------------------------------------------------------

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
    """Accept a datetime or an ISO-8601 string (as save_model records
    trained_at via datetime.isoformat()); return a NAIVE-UTC datetime for the
    datetime2 bind param. Tz-aware values are converted to UTC then stripped:
    SQL Server datetime2 has no offset, pymssql's handling of tz-aware binds
    is not something the fail-open hook would ever surface if it broke (the
    row write would silently no-op in production), and the sibling writer
    (ingest_verify) avoids Python-side datetimes entirely for this reason.
    Non-parseable / None pass through unchanged (best-effort)."""
    if isinstance(val, str):
        try:
            val = datetime.fromisoformat(val)
        except ValueError:
            return val
    if isinstance(val, datetime) and val.tzinfo is not None:
        return val.astimezone(timezone.utc).replace(tzinfo=None)
    return val


def values_from_metadata(metadata: dict) -> dict:
    """Extract the ModelTrainingRuns field values from the metadata dict
    ``save_model`` builds. Reused by both the save_model hook and the backfill
    script so train-time and backfill rows are populated identically.

    ``decision_promoted`` is sourced from ``metadata["promoted"]`` -- the gate's
    verdict at train time (what save_model wrote). ``Promoted`` (the live
    pointer) is NOT derived here; it is set separately by sync_promoted_flags
    from promoted.json.
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


# ---------------------------------------------------------------------------
# Write primitives
# ---------------------------------------------------------------------------

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
    """UPSERT one ModelTrainingRuns row keyed on the UNIQUE ``version``.

    UPDATE first; if it matched no row, INSERT. This makes a retrain that
    re-emits an existing version idempotent and crash-free (never a blind
    INSERT that would trip the unique constraint). ``Promoted`` is intentionally
    left untouched -- owned by sync_promoted_flags. Returns the version string.

    Raises on a genuine DB error (caller decides fail-open vs propagate).
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
    """Make the ``Promoted`` column truthful: exactly the row whose Version ==
    ``live_version`` gets Promoted=1, every other row gets 0. Idempotent
    (re-running is a no-op), two statements. ``live_version`` is the pointer
    read from promoted.json AFTER the promotion decision completes -- never a
    metadata ``promoted`` field. If no row matches live_version yet, all rows go
    to 0 (truthful: nothing is flagged live until its row exists).

    Raises on a genuine DB error (caller decides fail-open vs propagate).
    """
    lv = _truncate(live_version, _VERSION_CAP)
    with engine.begin() as conn:
        conn.execute(_SYNC_CLEAR_SQL, {"live_version": lv})
        conn.execute(_SYNC_SET_SQL, {"live_version": lv})
    logger.info("training_log.sync_promoted_flags: live_version=%s", lv)
