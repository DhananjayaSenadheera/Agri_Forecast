"""Feature-build run audit log (build_features.py -> ``IngestionRuns``,
Source="FEATURE_BUILD").

Until this module existed, the feature build (the one pipeline step run
after ingestion + verification and before training) left NO row in the
admin Ingestion runs log -- every ingestion source got a Running/Succeeded/
Failed breadcrumb (PR: ingestion run tracking foundation) but build_features
was invisible. This module is the sanctioned Python writer of a
``FEATURE_BUILD`` row into that same table, mirroring the two established
precedents:

* ``training_log.py`` -- the pymssql/tz-aware-datetime trap this module
  follows exactly: SQL Server ``datetime2`` has no offset, so every bound
  datetime is normalized to NAIVE UTC before it reaches the driver (see
  ``_to_naive_utc``; a tz-aware bind does not raise, it silently no-ops in
  production, which is why this is enforced unconditionally rather than
  trusted to the caller).
* ``ingest_verify.persist_verdict`` -- parameterized SQL over a SQLAlchemy
  engine (no ORM), and the FAIL-OPEN posture lives in the CALLER
  (build_features.py), not here: these two write primitives RAISE on a
  genuine DB error exactly like ``training_log.upsert_training_run`` /
  ``sync_promoted_flags`` do, so they stay unit-testable for correctness,
  and the entrypoint wraps both calls in try/except (see
  ``registry._record_training_run`` for the analogous outer guard).

Column contract (IngestionRuns, schema owned by the .NET migration
``CreateIngestionRunsAndVerifications``): Id/BatchId uniqueidentifier;
Source nvarchar(100); StartedUtc/FinishedUtc/CreatedAtUtc datetime2;
Status int; CoveredFromDate/CoveredToDate date (nullable);
RowsFetched/RowsInserted/RowsSkipped/DistinctCrops int (nullable);
ErrorSummary nvarchar(1000) (nullable). See
``AgriForecast.Domain/Entities/IngestionRun.cs``.

Status ints mirror ``AgriForecast.Domain.Enums.IngestionRunStatus``
(src/AgriForecast.Domain/Enums/IngestionRunStatus.cs) exactly -- that enum's
own docstring pins the numeric values (``HasConversion<int>``; a reorder
would silently corrupt persisted rows and must go through a migration, never
a Python-side change). This writer only ever emits Running -> Succeeded /
Failed (Skipped is defined for schema symmetry, unused here -- a feature
build is never a deliberate no-op the way a disabled ingestion source is).

RowsFetched is intentionally always NULL for this source: build_features
does not "fetch" rows from an external feed the way an ingestion source
does, it PERSISTS engineered rows (``RowsInserted``) from already-ingested
data -- RowsFetched would be a fabricated number with no honest meaning
here, and NULL alongside real RowsInserted/DistinctCrops/coverage is exactly
the "un-migrated / partially-reporting source" shape the schema already
supports (see IngestionRun.cs's own comment: "All nullable so an
un-migrated source can write a status-only row.").
"""
from __future__ import annotations

import logging
import uuid
from datetime import date, datetime, timezone

import sqlalchemy as sa

logger = logging.getLogger(__name__)

# Mirrors AgriForecast.Domain.Enums.IngestionRunStatus 1:1 (see
# src/AgriForecast.Domain/Enums/IngestionRunStatus.cs). NEVER reorder --
# the C# side pins these ints by test; a change belongs in a migration.
STATUS_RUNNING = 0
STATUS_SUCCEEDED = 1
STATUS_FAILED = 2
STATUS_SKIPPED = 3  # unused by this writer; defined for enum symmetry only

SOURCE = "FEATURE_BUILD"

_ERROR_SUMMARY_CAP = 1000


def _to_naive_utc(value: "datetime | None") -> "datetime | None":
    """Tz-aware -> naive UTC for a datetime2 bind param. SQL Server's
    datetime2 has no offset and pymssql's handling of a tz-aware bind is not
    something a fail-open hook would ever surface if it broke (the write
    would silently no-op in production) -- so this is applied unconditionally
    to every datetime this module sends, exactly as
    ``training_log._to_datetime`` does for ModelTrainingRuns.TrainedAtUtc.
    None-safe; already-naive values pass through unchanged.
    """
    if value is None:
        return None
    if value.tzinfo is not None:
        return value.astimezone(timezone.utc).replace(tzinfo=None)
    return value


_INSERT_SQL = sa.text(
    """INSERT INTO IngestionRuns
         (Id, BatchId, Source, StartedUtc, FinishedUtc, Status,
          CoveredFromDate, CoveredToDate, RowsFetched, RowsInserted, RowsSkipped,
          DistinctCrops, ErrorSummary, CreatedAtUtc)
       VALUES
         (:id, :batch_id, :source, :started_utc, NULL, :status,
          NULL, NULL, NULL, NULL, NULL,
          NULL, NULL, :created_at_utc)"""
)

_UPDATE_SQL = sa.text(
    """UPDATE IngestionRuns
          SET FinishedUtc    = :finished_utc,
              Status          = :status,
              CoveredFromDate = :covered_from,
              CoveredToDate   = :covered_to,
              RowsFetched     = :rows_fetched,
              RowsInserted    = :rows_inserted,
              RowsSkipped     = :rows_skipped,
              DistinctCrops   = :distinct_crops,
              ErrorSummary    = :error_summary
        WHERE Id = :id"""
)


def start_run(engine) -> "tuple[uuid.UUID, uuid.UUID, datetime]":
    """Insert a Running row for one feature-build pass. Returns
    ``(run_id, batch_id, started_utc)`` -- ``run_id`` is threaded back into
    ``mark_succeeded``/``mark_failed`` to finalize the SAME row.

    BatchId is always a fresh uuid4: build_features.py runs standalone,
    already after ingestion + verification have finished their own pass, so
    there is no live .NET Worker BatchId to thread through (contrast
    ``verify_ingestion.py --batch-id``, which IS threaded via a shell
    ``BATCH_ID`` env var from run-daily.sh -- that plumbing is deliberately
    NOT extended here; adding it would be an orchestration change this task
    does not require, and a build's own row need not share a pass's BatchId
    to be honest).

    StartedUtc/CreatedAtUtc are stamped Python-side (naive UTC) rather than
    via ``SYSUTCDATETIME()`` -- unlike ``ingest_verify.persist_verdict``'s
    one-shot insert, this row is finalized by a LATER statement in the same
    process, so the caller needs the exact started instant back (e.g. to log
    duration) rather than relying on a value only the server knows.

    Raises on a genuine DB error -- fail-open is the CALLER's responsibility,
    exactly like ``training_log.upsert_training_run`` /
    ``sync_promoted_flags``.
    """
    run_id = uuid.uuid4()
    batch_id = uuid.uuid4()
    started_utc = _to_naive_utc(datetime.now(timezone.utc))
    with engine.begin() as conn:
        conn.execute(_INSERT_SQL, {
            "id": str(run_id),
            "batch_id": str(batch_id),
            "source": SOURCE,
            "started_utc": started_utc,
            "status": STATUS_RUNNING,
            "created_at_utc": started_utc,
        })
    logger.info(
        "feature_run_log.start_run: inserted Running row run_id=%s batch_id=%s",
        run_id, batch_id,
    )
    return run_id, batch_id, started_utc


def mark_succeeded(
    engine,
    run_id: uuid.UUID,
    *,
    rows_inserted: "int | None" = None,
    distinct_crops: "int | None" = None,
    covered_from: "date | None" = None,
    covered_to: "date | None" = None,
) -> None:
    """Finalize ``run_id`` as Succeeded. All counts/coverage are optional and
    None-safe -- an empty build (zero feature rows) still lands an honest
    Succeeded row with RowsInserted=0 rather than blocking on it.

    Raises on a genuine DB error -- caller (build_features.py) fail-opens.
    """
    finished_utc = _to_naive_utc(datetime.now(timezone.utc))
    with engine.begin() as conn:
        conn.execute(_UPDATE_SQL, {
            "id": str(run_id),
            "finished_utc": finished_utc,
            "status": STATUS_SUCCEEDED,
            "covered_from": covered_from,
            "covered_to": covered_to,
            "rows_fetched": None,
            "rows_inserted": rows_inserted,
            "rows_skipped": None,
            "distinct_crops": distinct_crops,
            "error_summary": None,
        })
    logger.info(
        "feature_run_log.mark_succeeded: run_id=%s rows_inserted=%s distinct_crops=%s "
        "covered=[%s..%s]",
        run_id, rows_inserted, distinct_crops, covered_from, covered_to,
    )


def mark_failed(engine, run_id: uuid.UUID, exc: Exception) -> None:
    """Finalize ``run_id`` as Failed. ErrorSummary = ``str(exc)`` capped to
    the 1000-char column (per the task brief -- a plain str(exc), not the
    C# entity's "TypeName: message" convention, since the caller already
    knows the exception type from its own except clause / logs).

    Raises on a genuine DB error -- caller fail-opens AND still re-raises
    the ORIGINAL build exception either way (the pipeline must fail loudly
    even if this bookkeeping write itself fails).
    """
    finished_utc = _to_naive_utc(datetime.now(timezone.utc))
    error_summary = str(exc)[:_ERROR_SUMMARY_CAP]
    with engine.begin() as conn:
        conn.execute(_UPDATE_SQL, {
            "id": str(run_id),
            "finished_utc": finished_utc,
            "status": STATUS_FAILED,
            "covered_from": None,
            "covered_to": None,
            "rows_fetched": None,
            "rows_inserted": None,
            "rows_skipped": None,
            "distinct_crops": None,
            "error_summary": error_summary,
        })
    logger.info("feature_run_log.mark_failed: run_id=%s error=%s", run_id, error_summary)
