using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Repositories;

// ISOLATION + FAIL-SAFE (mirrors IngestionRunRepository's B1 discipline): every audit write runs in
// its OWN service scope, resolving a FRESH AgriForecastDbContext with an independent ChangeTracker,
// so an audit SaveChanges can never flush the caller's (login/registration/admin) pending entities,
// and a poisoned entity in the caller's context can never make an audit save throw.
//
// FAIL-SAFE: an audit write must NEVER break the operation it records. Every write is wrapped in a
// try/catch that SWALLOWS-AND-LOGS — the auth/user handlers await the call but it can never throw
// into them. The clock is stamped here (DateTime.UtcNow) at write time.
public class UserActivityAudit : IUserActivityAudit
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<UserActivityAudit> _logger;

    public UserActivityAudit(IServiceScopeFactory scopeFactory, ILogger<UserActivityAudit> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task RecordLoginSucceededAsync(Guid actorUserId, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.LoginSucceeded(actorUserId, DateTime.UtcNow), ct);

    public Task RecordLoginFailedAsync(string? usernameAttempted, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.LoginFailed(usernameAttempted, DateTime.UtcNow), ct);

    public Task RecordUserRegisteredAsync(Guid actorUserId, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.UserRegistered(actorUserId, DateTime.UtcNow), ct);

    public Task RecordRoleChangedAsync(Guid actorUserId, Guid targetUserId, string newRole, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.RoleChanged(actorUserId, targetUserId, $"role -> {newRole}", DateTime.UtcNow), ct);

    public Task RecordUserDeletedAsync(Guid actorUserId, Guid targetUserId, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.UserDeleted(actorUserId, targetUserId, DateTime.UtcNow), ct);

    // Single isolated, swallow-and-log write path shared by every event. Never rethrows.
    private async Task WriteAsync(UserActivityEvent activityEvent, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AgriForecastDbContext>();
            await db.UserActivityLog.AddAsync(activityEvent, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Log the event TYPE only (never the attempted username / details) and swallow — the
            // audited operation must succeed even if its audit trail could not be written.
            _logger.LogWarning(ex, "Failed to write user-activity audit row for {EventType}.",
                activityEvent.EventType);
        }
    }
}
