using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
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

    public Task RecordUserCreatedByAdminAsync(Guid actingAdminId, Guid newUserId, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.UserCreatedByAdmin(actingAdminId, newUserId, DateTime.UtcNow), ct);

    public Task RecordRoleChangedAsync(Guid actorUserId, Guid targetUserId, string newRole, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.RoleChanged(actorUserId, targetUserId, $"role -> {newRole}", DateTime.UtcNow), ct);

    public Task RecordUserDeletedAsync(Guid actorUserId, Guid targetUserId, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.UserDeleted(actorUserId, targetUserId, DateTime.UtcNow), ct);

    // ── Admin CONTENT mutations ───────────────────────────────────────────────────────────────────

    public Task RecordPolicyFlagChangedAsync(
        Guid actingAdminId, ContentChangeAction action, string? identifier, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.PolicyFlagChanged(
            actingAdminId, RenderDetails(action, identifier), DateTime.UtcNow), ct);

    public Task RecordFestivalChangedAsync(
        Guid actingAdminId, ContentChangeAction action, string? identifier, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.FestivalChanged(
            actingAdminId, RenderDetails(action, identifier), DateTime.UtcNow), ct);

    public Task RecordNewsEventChangedAsync(
        Guid actingAdminId, ContentChangeAction action, string? identifier, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.NewsEventChanged(
            actingAdminId, RenderDetails(action, identifier), DateTime.UtcNow), ct);

    public Task RecordCropChangedAsync(
        Guid actingAdminId, ContentChangeAction action, string? identifier, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.CropChanged(
            actingAdminId, RenderDetails(action, identifier), DateTime.UtcNow), ct);

    public Task RecordMarketChangedAsync(
        Guid actingAdminId, ContentChangeAction action, string? identifier, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.MarketChanged(
            actingAdminId, RenderDetails(action, identifier), DateTime.UtcNow), ct);

    // The ONE place the content Details note is rendered: "<verb> '<identifier>'" (e.g.
    // "updated 'GARLIC-IMPORT-BAN'"). Rendering here — not at the thirteen call sites — is what keeps
    // the note format identical everywhere and greppable by the admin Logs page.
    //
    // The identifier is trimmed and hard-capped to IdentifierMaxLength, which keeps the whole note
    // WELL under the 500-char Details column even before the entity's own cap (an admin can type a
    // very long title; the audit trail only needs enough to recognise the row). A blank identifier
    // renders the bare verb rather than an empty-quoted "created ''".
    public static string RenderDetails(ContentChangeAction action, string? identifier)
    {
        var verb = action switch
        {
            ContentChangeAction.Created => "created",
            ContentChangeAction.Updated => "updated",
            ContentChangeAction.Deleted => "deleted",
            _ => action.ToString().ToLowerInvariant()
        };

        if (string.IsNullOrWhiteSpace(identifier))
            return verb;

        var trimmed = identifier.Trim();
        if (trimmed.Length > IdentifierMaxLength)
            trimmed = trimmed[..IdentifierMaxLength];

        return $"{verb} '{trimmed}'";
    }

    // Deliberately far below the Details column's 500: these are handles (titles/keys/codes), not
    // content. Leaves ample headroom for the verb + quotes under any future note prefix.
    private const int IdentifierMaxLength = 120;

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
