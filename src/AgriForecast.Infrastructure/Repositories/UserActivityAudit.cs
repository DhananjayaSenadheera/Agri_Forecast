using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Repositories;

// Every audit write runs in its OWN service scope with a fresh DbContext, so an audit SaveChanges can never
// flush the caller's pending entities, and a poisoned entity in the caller's context can never make an audit
// save throw.
// An audit write must never break the operation it records: every write is wrapped in a try/catch that
// swallows-and-logs, so the awaiting handlers can never see it throw. The clock is stamped here at write time.
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

    public Task RecordIngestionServiceStartedAsync(
        Guid actingAdminId, Guid batchId, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.IngestionServiceStarted(
            actingAdminId, RenderBatchDetails(batchId), DateTime.UtcNow), ct);

    public Task RecordIngestionServiceStopRequestedAsync(
        Guid actingAdminId, Guid batchId, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.IngestionServiceStopRequested(
            actingAdminId, RenderBatchDetails(batchId), DateTime.UtcNow), ct);

    // The farmer's own event. details arrives already rendered ("<CropCode> · <Reason word>") and carries no
    // free text the farmer typed; the note stays on the PlantedDateRemovals row.
    public Task RecordPlantedDateRemovedAsync(
        Guid actorUserId, string? details, CancellationToken ct = default) =>
        WriteAsync(UserActivityEvent.PlantedDateRemoved(actorUserId, details, DateTime.UtcNow), ct);

    // The one place a pipeline-control Details note is rendered: "batch <guid>". Guid.ToString() is
    // lowercase, matching the casing every other batchId is written and searched in.
    public static string RenderBatchDetails(Guid batchId) => $"batch {batchId}";

    // The one place the content Details note is rendered: "<verb> '<identifier>'". Rendering it here rather
    // than at the thirteen call sites keeps the format identical everywhere and greppable by the Logs page.
    // The identifier is trimmed and capped to IdentifierMaxLength, well under the 500-char Details column.
    // A blank identifier renders the bare verb rather than "created ''".
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

    // Deliberately far below the Details column's 500: these are handles (titles, keys, codes), not content.
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
            // Log the event type only — never the attempted username or details — and swallow, so the audited
            // operation still succeeds even if its audit trail could not be written.
            _logger.LogWarning(ex, "Failed to write user-activity audit row for {EventType}.",
                activityEvent.EventType);
        }
    }
}
