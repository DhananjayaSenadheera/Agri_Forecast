namespace AgriForecast.Application.Services;

// Cross-PROCESS single-flight guard for an ingestion pass.
//
// Why not a lock/SemaphoreSlim: the competing pass normally runs in another process entirely — the
// Kubernetes CronJob / Docker ingestion worker — which an in-process lock cannot see. Two passes over the
// same sources at once would double-fetch every source and interleave their watermark writes.
//
// Why not "is there a Running row": an IngestionRuns row is a STATUS SIGNAL, not a mutex. It is written
// best-effort (the audit deliberately swallows its own failures), it can be missing for a pass that is
// genuinely running, and it can linger for a pass that has crashed. It is checked as a fast, friendly
// pre-check, never as the exclusion mechanism.
//
// The implementation is a SQL Server session-scoped application lock, so the database — the one thing
// every host shares — arbitrates. Acquisition NEVER waits (timeout 0): a held lock means "already running
// somewhere", which the caller reports immediately rather than queueing a second pass behind the first.
//
// BOTH pass-running hosts must acquire it or it guards nothing: the API's start handler (refuses with
// 409 already_running) and the Ingestion Worker (logs and SKIPS that scheduled pass). Adding a third
// caller of IIngestionPassRunner without a lease silently re-opens concurrent passes.
public interface IIngestionPassLock
{
    // Attempts to take the pass lock. Returns the lease on success, or null when it is already held
    // (fail fast, no waiting). Dispose the lease to release; disposal must be safe to call exactly once.
    Task<IIngestionPassLease?> TryAcquireAsync(CancellationToken ct = default);
}

// The held lock. The pass holds this for its whole duration, so it must be disposed in a finally — an
// undisposed lease would wedge every later pass until the SQL session dies.
public interface IIngestionPassLease : IAsyncDisposable
{
}
