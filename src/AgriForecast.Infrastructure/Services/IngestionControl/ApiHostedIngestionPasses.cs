using AgriForecast.Application.Services;

namespace AgriForecast.Infrastructure.Services.IngestionControl;

// Process-wide registry of the ingestion pass this host started, so the admin stop button has a
// CancellationTokenSource to trip. Registered SINGLETON — a per-request instance would forget the pass the
// moment the start request's 202 was written, and every stop would report "nothing running".
//
// The applock already guarantees at most one pass at a time, so at most one handle is ever current. The
// gate below is defensive rather than load-bearing: Begin while one is current still replaces the current
// handle, but the outgoing one is left cancellable by its own owner and simply stops being the stop
// target. All state changes take the same monitor so a concurrent stop cannot observe a half-swapped handle.
public class ApiHostedIngestionPasses : IApiHostedIngestionPasses
{
    private readonly object _gate = new();
    private PassHandle? _current;

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _current is not null;
        }
    }

    public IApiHostedPassHandle Begin(Guid batchId)
    {
        var handle = new PassHandle(this, batchId);
        lock (_gate)
        {
            _current = handle;
        }
        return handle;
    }

    public bool TryRequestStop(out Guid batchId)
    {
        PassHandle? handle;
        lock (_gate)
        {
            handle = _current;
        }

        if (handle is null)
        {
            batchId = Guid.Empty;
            return false;
        }

        // Cancel OUTSIDE the monitor: continuations registered on the token run inline on Cancel(), and
        // running them while holding the gate invites a deadlock against Begin/Dispose.
        // A pass that finished between the read and here has already disposed its CTS; that race is a
        // no-op stop, not an error, so the ObjectDisposedException is swallowed — and batchId stays empty,
        // because reporting an id for a stop that did not happen would put a false row in the audit trail.
        try
        {
            handle.Cancel();
        }
        catch (ObjectDisposedException)
        {
            batchId = Guid.Empty;
            return false;
        }

        batchId = handle.BatchId;
        return true;
    }

    // Clears the current handle if it is still this one. A pass that was superseded must not wipe its
    // successor's registration when it finally finishes.
    private void Release(PassHandle handle)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, handle))
                _current = null;
        }
    }

    private sealed class PassHandle : IApiHostedPassHandle
    {
        private readonly ApiHostedIngestionPasses _owner;
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        public PassHandle(ApiHostedIngestionPasses owner, Guid batchId)
        {
            _owner = owner;
            BatchId = batchId;
        }

        public Guid BatchId { get; }

        public CancellationToken Token => _cts.Token;

        public void Cancel() => _cts.Cancel();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Release(this);
            _cts.Dispose();
        }
    }
}
