namespace AgriForecast.Application.Services;

// Registry of ingestion passes started by THIS API process, so the admin stop button has something it can
// actually cancel.
//
// The honest limitation, deliberately part of the wire contract: stop can only reach a pass this process
// started. The scheduled CronJob / Docker worker runs in another process with no inbound control channel,
// so a pass started there is reported as "not_stoppable" rather than pretending a stop succeeded. Adding
// a real cross-process stop means the ingestion microservice + message broker phase, not a lie here.
//
// Implemented as a process-wide singleton holding one CancellationTokenSource. The applock already
// guarantees at most one pass at a time, so "the current pass" is unambiguous; the implementation is still
// written to be safe if that ever stops holding.
public interface IApiHostedIngestionPasses
{
    // Begins tracking a pass about to run on this host and returns its handle. The handle owns the
    // CancellationTokenSource; run the pass under handle.Token and dispose the handle when the pass ends
    // (disposal also de-registers it, so a finished pass is never reported as stoppable).
    IApiHostedPassHandle Begin(Guid batchId);

    // True while a pass started by this process is still running.
    bool IsRunning { get; }

    // Signals cancellation to the pass hosted here and reports its batchId. False when none is hosted —
    // the caller then decides between "not_running" and "not_stoppable" from the DB-derived state.
    // Signalling is best-effort and returns immediately: the pass finishes its in-flight source and stops
    // before the next one, so a true here means "stop requested", never "stopped".
    bool TryRequestStop(out Guid batchId);
}

// A tracked, cancellable pass. Disposing de-registers it.
public interface IApiHostedPassHandle : IDisposable
{
    Guid BatchId { get; }

    // The token the pass must run under. Cancelled by TryRequestStop.
    CancellationToken Token { get; }
}
