using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Repositories;

// Every error-log write runs in its OWN service scope with a fresh DbContext, so it can never flush the
// request's pending entities, and a poisoned entity in the request context can never make the log save throw.
//
// Recording an error must never change the response being recorded, so every write is wrapped in a catch-all
// that logs ONLY the failure's type name — never the exception message, the request path, or any captured
// field — and never rethrows into the middleware.
//
// This writer is a SINGLETON, unlike the Scoped UserActivityAudit, so the storm-guard window and the
// retention counter are process-wide instance state. That is safe because it depends only on singleton-safe
// seams and self-scopes every DB access, and it lets GlobalExceptionMiddleware constructor-inject it.
//
// Storm guard: a process-wide token bucket admits at most MaxWritesPerWindow writes per rolling minute;
// excess writes are dropped, not queued, and one log reports the drop count when a window rolls over.
// Retention: roughly every PruneEveryN successful inserts, a bounded DELETE trims rows older than 90 days.
// The prune is self-guarded, so a prune failure never throws and never masks a successful insert.
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
            // Log only the failure's type name — never the recorded exception or the request path — and
            // swallow: error logging must never change the response.
            _logger.LogWarning("Failed to write system-error log row ({FailureType}).",
                failure.GetType().Name);
        }
    }

    // Process-wide token bucket. Returns false when the window is full and the write must be dropped; on a
    // window roll-over it emits one log per window that had drops.
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

    // Every Nth successful insert, run the bounded age-based prune in its own scope. Self-guarded: a prune
    // failure is swallowed and logged and never masks the insert.
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
