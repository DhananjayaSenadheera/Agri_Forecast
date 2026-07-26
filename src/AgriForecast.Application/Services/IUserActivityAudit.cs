using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Services;

// Fire-safe audit seam for security-relevant account events. The implementation writes one UserActivityLog
// row per call in its own service scope and swallows-and-logs any failure — an audit write must never add
// a failure mode to login, registration or an admin operation.
// The methods take only primitives the call sites already hold; never a password, token, header or body.
public interface IUserActivityAudit
{
    // Successful credential check + token issue. actorUserId = the user who logged in.
    Task RecordLoginSucceededAsync(Guid actorUserId, CancellationToken ct = default);

    // Failed credential check. usernameAttempted is the attempted username — the security signal, never the
    // password. No actor, because the caller is unproven.
    Task RecordLoginFailedAsync(string? usernameAttempted, CancellationToken ct = default);

    // A new account was self-registered. actorUserId = the new user's id.
    Task RecordUserRegisteredAsync(Guid actorUserId, CancellationToken ct = default);

    // An admin provisioned an account. Shares the UserRegistered event type but is told apart by its shape:
    // the actor is the admin and the target is the new user, where a self-registration has no target.
    Task RecordUserCreatedByAdminAsync(Guid actingAdminId, Guid newUserId, CancellationToken ct = default);

    // An admin changed a user's role. Details is rendered as "role -> <newRole>".
    Task RecordRoleChangedAsync(Guid actorUserId, Guid targetUserId, string newRole, CancellationToken ct = default);

    // An admin deleted a user.
    Task RecordUserDeletedAsync(Guid actorUserId, Guid targetUserId, CancellationToken ct = default);

    // Content mutations: one method per mutable entity kind, with the verb as a parameter (create, update
    // and delete share the event type and are told apart by the rendered Details). Every one is called
    // AFTER a successful commit and is fail-open, exactly like the account events above.
    // identifier is a short handle the admin already sees in the UI — a title, festival key plus date, or
    // crop code — never a request body, description or URL. The implementation trims and caps it.

    // A policy flag was created/updated/deleted (identifier = its Title).
    Task RecordPolicyFlagChangedAsync(
        Guid actingAdminId, ContentChangeAction action, string? identifier, CancellationToken ct = default);

    // A festival-calendar entry was created/updated/deleted (identifier = "<FestivalKey> yyyy-MM-dd").
    Task RecordFestivalChangedAsync(
        Guid actingAdminId, ContentChangeAction action, string? identifier, CancellationToken ct = default);

    // A news event was created/updated/deleted (identifier = its Title).
    Task RecordNewsEventChangedAsync(
        Guid actingAdminId, ContentChangeAction action, string? identifier, CancellationToken ct = default);

    // A crop was created/updated/deleted (identifier = its CropCode, e.g. "VEG000071").
    Task RecordCropChangedAsync(
        Guid actingAdminId, ContentChangeAction action, string? identifier, CancellationToken ct = default);

    // A market or economic centre was registered (identifier = its Name). The verb parameter is kept so
    // adding an update or delete needs no interface change.
    Task RecordMarketChangedAsync(
        Guid actingAdminId, ContentChangeAction action, string? identifier, CancellationToken ct = default);

    // Pipeline control. Called AFTER the action has been accepted (the pass was started / the cancellation
    // was signalled) and fail-open, like everything above — an unwritable audit row must never turn an
    // accepted start into an error the admin sees.
    // batchId ties the control action to the IngestionRuns rows of the pass it governs.

    // An admin started an ingestion pass on this API host.
    Task RecordIngestionServiceStartedAsync(
        Guid actingAdminId, Guid batchId, CancellationToken ct = default);

    // An admin asked to stop the pass hosted here. Recorded as REQUESTED, not "stopped": the API signals
    // cancellation between sources and never witnesses the pass end.
    Task RecordIngestionServiceStopRequestedAsync(
        Guid actingAdminId, Guid batchId, CancellationToken ct = default);
}
