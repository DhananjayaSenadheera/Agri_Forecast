using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Repositories;

// ISOLATION + FAIL-SAFE (mirrors UserActivityAudit's B1 discipline): every error-log write runs in its
// OWN service scope, resolving a FRESH AgriForecastDbContext with an independent ChangeTracker, so an
// error-log SaveChanges can never flush the request's pending entities, and a poisoned entity in the
// request context can never make the error-log save throw.
//
// FAIL-SAFE: recording an error must NEVER change the response being recorded. Every write is wrapped
// in a catch-all that SWALLOWS-AND-LOGS ONLY THE FAILURE'S TYPE NAME (never the exception message, the
// request path, or any captured field) and NEVER rethrows into the middleware.
//
// LIFETIME (deliberate deviation from UserActivityAudit's Scoped registration): this writer is a
// SINGLETON so the storm-guard window and the retention counter are naturally PROCESS-WIDE instance
// state. It is safe as a singleton because it depends only on singleton-safe seams (IServiceScopeFactory,
// ILogger, TimeProvider) and self-scopes every DB access — it captures no scoped dependency. Being a
// singleton also lets GlobalExceptionMiddleware constructor-inject it directly.
//
// STORM GUARD: a process-wide token bucket admits at most MaxWritesPerWindow (60) writes per rolling
// one-minute window; excess writes are DROPPED (not queued). When a window with drops rolls over, one
// stdout log reports the drop count for that window.
//
// RETENTION: after roughly every PruneEveryN (100) successful inserts, a bounded DELETE TOP (500) trims
// rows older than 90 days. The prune is self-guarded (its own scope + try/catch) so a prune failure can
// never throw and never masks a successful insert.
public class SystemErrorLog : ISystemErrorLog
{
    private const string SourceApi = "API";
    private const int MaxWritesPerWindow = 60;
    private const int PruneEveryN = 100;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    // Constant SQL (no interpolation): bounded, index-friendly age-based prune of the oldest rows.
    private const string PruneSql =
        "DELETE TOP (500) FROM SystemErrors WHERE OccurredUtc < DATEADD(day, -90, SYSUTCDATETIME())";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SystemErrorLog> _logger;
    private readonly TimeProvider _clock;

    // Storm-guard window state (process-wide; this writer is a singleton). Guarded by _stormLock.
    private readonly object _stormLock = new();
    private DateTime _windowStart;
    private int _windowCount;
    private int _windowDropped;
    private bool _windowInitialized;

    // Rolling count of successful inserts, for the every-Nth retention prune (no randomness).
    private long _insertCount;

    public SystemErrorLog(
        IServiceScopeFactory scopeFactory,
        ILogger<SystemErrorLog> logger,
        TimeProvider? clock = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task RecordAsync(
        Exception ex, string method, string path, string? traceId, CancellationToken ct)
    {
        try
        {
            if (!TryAdmit())
                return;

            var occurredUtc = _clock.GetUtcNow().UtcDateTime;
            var row = SystemError.FromException(ex, SourceApi, method, path, traceId, occurredUtc);

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgriForecastDbContext>();
            await db.SystemErrors.AddAsync(row, ct);
            await db.SaveChangesAsync(ct);

            await MaybePruneAsync(ct);
        }
        catch (Exception failure)
        {
            // Log ONLY the failure's type name — never the recorded exception's message/stack, and
            // never the request path — and swallow: error logging must never change the response.
            _logger.LogWarning("Failed to write system-error log row ({FailureType}).",
                failure.GetType().Name);
        }
    }

    // Process-wide token bucket. Returns true if this write is admitted, false if the window is full and
    // the write must be dropped. On a window roll-over, emits one stdout log per window that had drops.
    private bool TryAdmit()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        lock (_stormLock)
        {
            if (!_windowInitialized || now - _windowStart >= Window)
            {
                if (_windowInitialized && _windowDropped > 0)
                    _logger.LogWarning(
                        "SystemErrorLog storm guard dropped {DroppedCount} error-log write(s) in the last minute.",
                        _windowDropped);

                _windowStart = now;
                _windowCount = 0;
                _windowDropped = 0;
                _windowInitialized = true;
            }

            if (_windowCount >= MaxWritesPerWindow)
            {
                _windowDropped++;
                return false;
            }

            _windowCount++;
            return true;
        }
    }

    // Every Nth successful insert, run the bounded age-based prune in its OWN scope. Fully self-guarded:
    // a prune failure is swallowed-and-logged (type name only) and never throws, never masks the insert.
    private async Task MaybePruneAsync(CancellationToken ct)
    {
        if (Interlocked.Increment(ref _insertCount) % PruneEveryN != 0)
            return;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgriForecastDbContext>();
            await db.Database.ExecuteSqlRawAsync(PruneSql, ct);
        }
        catch (Exception failure)
        {
            _logger.LogWarning("System-error retention prune failed ({FailureType}).",
                failure.GetType().Name);
        }
    }
}
