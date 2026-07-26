"""Feature-build run audit log: writes an IngestionRuns row with Source='FEATURE_BUILD'.

Every ingestion source leaves a Running/Succeeded/Failed breadcrumb in the admin
Ingestion runs log; this module is the sanctioned Python writer of the equivalent
row for the feature build. The exact Source string, casing included, is part of the
contract with the .NET side.

Every datetime is normalised to naive UTC before binding: SQL Server datetime2 has
no offset, and a tz-aware bind does not raise, it silently no-ops in production.

Both write primitives raise on a genuine DB error - fail-open is the CALLER's job
(build_features.py wraps the calls), so this bookkeeping can never take down the
real pipeline step.

Status ints mirror AgriForecast.Domain.Enums.IngestionRunStatus. RowsFetched is
always NULL for this source: the build persists engineered rows rather than
fetching from a feed, and any number there would be fabricated.
"""
from __future__ import annotations

import logging
import uuid
from datetime import date, datetime, timezone

import sqlalchemy as sa

logger = logging.getLogger(__name__)

# Mirrors AgriForecast.Domain.Enums.IngestionRunStatus 1:1. Never reorder: these ints
# are persisted, so any change belongs in a migration, not here.
STATUS_RUNNING = 0
STATUS_SUCCEEDED = 1
STATUS_FAILED = 2
STATUS_SKIPPED = 3  # unused by this writer; defined for enum symmetry only

SOURCE = "FEATURE_BUILD"

_ERROR_SUMMARY_CAP = 1000


def _to_naive_utc(value: "datetime | None") -> "datetime | None":
    """Convert a tz-aware datetime to naive UTC for a datetime2 bind param.

    SQL Server datetime2 has no offset and a tz-aware bind would silently no-op in
    production, so this is applied to every datetime this module sends. None-safe, and
    already-naive values pass through unchanged.
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
    """Insert a Running row for one feature-build pass.

    Returns (run_id, batch_id, started_utc); run_id is passed back to mark_succeeded or
    mark_failed to finalise the SAME row. BatchId is always a fresh uuid4 because
    build_features.py runs standalone, after ingestion and verification have finished
    their own pass. StartedUtc is stamped Python-side so the caller gets the exact
    start instant back, since a later statement finalises the row.

    Raises on a genuine DB error - the caller fail-opens.
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
    """Finalise run_id as Succeeded.

    All counts and coverage are optional, so an empty build still lands an honest row
    with RowsInserted=0. Raises on a genuine DB error - build_features.py fail-opens.
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
    """Finalise run_id as Failed, with str(exc) capped to the 1000-char column.

    Raises on a genuine DB error. The caller fail-opens but still re-raises the
    original build exception, so the pipeline fails loudly either way.
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
