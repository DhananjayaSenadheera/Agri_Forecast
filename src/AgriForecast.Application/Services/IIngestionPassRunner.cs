namespace AgriForecast.Application.Services;

// One full ingestion pass: every source, in order, each wrapped in its own IngestionRuns audit row and
// fail-isolated so one bad source never aborts the others.
//
// This is the seam that used to be Worker.RunPassAsync. It was lifted here so BOTH hosts can run a pass —
// the Ingestion Worker (scheduled) and the API (admin-triggered start button) — from ONE implementation,
// rather than the API growing a second, drifting copy of the source sequence.
//
// Deliberately TRANSPORT-AGNOSTIC: no HTTP types, no message-broker types, no return payload beyond the
// batchId the caller already supplied. A later phase is expected to move ingestion out to its own service
// and replace the API's direct call with a "StartIngestion" message; when that happens only the caller
// changes, not this contract. Nothing here may assume it is invoked in-process.
//
// The runner does NOT enforce mutual exclusion — that is IIngestionPassLock's job, because the competing
// pass usually lives in a DIFFERENT process (the CronJob/Docker worker) where an in-process lock is blind.
public interface IIngestionPassRunner
{
    // Runs one pass under the given batchId; every source's run row shares it so the pass is reconstructible.
    // Honours ct BETWEEN sources: a cancelled pass stops launching further sources rather than being killed
    // mid-write, and the in-flight source sees the same token.
    // Never throws for a source failure (each source is audited as Failed instead).
    Task RunPassAsync(Guid batchId, CancellationToken ct);
}
