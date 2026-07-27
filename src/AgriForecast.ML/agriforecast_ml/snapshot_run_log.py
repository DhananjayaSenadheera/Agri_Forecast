"""Forecast-snapshot trigger run audit log: writes an IngestionRuns row with
Source='FORECAST_SNAPSHOT'.

Sibling of feature_run_log.py (same shape, same IngestionRuns table, same fail-open
contract): trigger_forecast_snapshot.py is the caller, exactly as build_features.py is
feature_run_log's caller. The exact Source string, casing included, is part of the
contract with the .NET side (IngestionSources.FORECAST_SNAPSHOT / KnownKeys).

Every datetime is normalised to naive UTC before binding: SQL Server datetime2 has
no offset, and a tz-aware bind does not raise, it silently no-ops in production.

Every write primitive here raises on a genuine DB error - fail-open is the CALLER's
job (trigger_forecast_snapshot.py wraps every call), so this bookkeeping can never
take down the real trigger.

Status ints mirror AgriForecast.Domain.Enums.IngestionRunStatus. RowsFetched here
carries the maturing pass's scanned-row count (not a fetch in the HTTP sense) since
there is no better-fitting column; see trigger_forecast_snapshot.py for the exact
field mapping from the /admin/snapshot-forecasts response.

mark_failed takes a plain human-readable STRING, not an Exception: unlike
FEATURE_BUILD (which only ever fails from a raised exception), this source's "Failed"
rows cover three different shapes of bad news - a transport error, a non-2xx/422 HTTP
response, and a 200 "ok" response that nonetheless reports per-crop/per-row failures
(the trap this whole audit row exists to make visible) - and the caller is in a better
position than an exception object to write the summary for each.
"""
from __future__ import annotations

import uuid
from datetime import date, datetime, timezone

import sqlalchemy as sa

# Mirrors AgriForecast.Domain.Enums.IngestionRunStatus 1:1. Never reorder: these ints
# are persisted, so any change belongs in a migration, not here.
STATUS_RUNNING = 0
STATUS_SUCCEEDED = 1
STATUS_FAILED = 2
STATUS_SKIPPED = 3

SOURCE = "FORECAST_SNAPSHOT"

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
    """Insert a Running row for one forecast-snapshot trigger attempt.

    Returns (run_id, batch_id, started_utc); run_id is passed back to mark_succeeded,
    mark_failed or mark_skipped to finalise the SAME row. BatchId is always a fresh
    uuid4 - the trigger runs standalone in the same container as build_features but
    after it has already finished and finalised its own row, so there is no shared
    batch to join.

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
    return run_id, batch_id, started_utc


def mark_succeeded(
    engine,
    run_id: uuid.UUID,
    *,
    rows_inserted: "int | None" = None,
    rows_skipped: "int | None" = None,
    rows_fetched: "int | None" = None,
    distinct_crops: "int | None" = None,
    covered_from: "date | None" = None,
    covered_to: "date | None" = None,
) -> None:
    """Finalise run_id as Succeeded: reserved for a genuinely clean pass (HTTP 200,
    status "ok", AND zero snapshotCropFailures AND zero matureRowFailures) - see
    trigger_forecast_snapshot.py's decision logic. Raises on a genuine DB error -
    the caller fail-opens.
    """
    finished_utc = _to_naive_utc(datetime.now(timezone.utc))
    with engine.begin() as conn:
        conn.execute(_UPDATE_SQL, {
            "id": str(run_id),
            "finished_utc": finished_utc,
            "status": STATUS_SUCCEEDED,
            "covered_from": covered_from,
            "covered_to": covered_to,
            "rows_fetched": rows_fetched,
            "rows_inserted": rows_inserted,
            "rows_skipped": rows_skipped,
            "distinct_crops": distinct_crops,
            "error_summary": None,
        })


def mark_failed(engine, run_id: uuid.UUID, summary: str) -> None:
    """Finalise run_id as Failed, with `summary` capped to the 1000-char column.

    `summary` is a plain string, not an Exception: the caller builds a
    human-readable message for whichever bad-news shape it hit (transport error,
    non-2xx/422 response, or a 200 "ok" response reporting per-crop/per-row
    failures). Raises on a genuine DB error - the caller fail-opens.
    """
    finished_utc = _to_naive_utc(datetime.now(timezone.utc))
    error_summary = str(summary)[:_ERROR_SUMMARY_CAP]
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


def mark_skipped(engine, run_id: uuid.UUID) -> None:
    """Finalise run_id as Skipped: the FORECAST_SNAPSHOTS_ENABLED flag is off this
    run. A deliberate no-op, never a failure - mirrors IngestionRunStatus.Skipped's
    documented meaning on the .NET side. Raises on a genuine DB error - the caller
    fail-opens.
    """
    finished_utc = _to_naive_utc(datetime.now(timezone.utc))
    with engine.begin() as conn:
        conn.execute(_UPDATE_SQL, {
            "id": str(run_id),
            "finished_utc": finished_utc,
            "status": STATUS_SKIPPED,
            "covered_from": None,
            "covered_to": None,
            "rows_fetched": None,
            "rows_inserted": None,
            "rows_skipped": None,
            "distinct_crops": None,
            "error_summary": None,
        })
