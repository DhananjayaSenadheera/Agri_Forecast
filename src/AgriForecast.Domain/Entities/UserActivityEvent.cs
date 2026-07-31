using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// One row per security-relevant account event or admin content mutation (table UserActivityLog). Written
// via IUserActivityAudit from the auth/user-management call sites and every admin content-mutation
// handler; read by the admin Logs hub. Content rows carry the acting admin as ActorUserId and no
// TargetUserId — the content's short identifier goes in Details.
// An audit failure must never break the operation being audited, so the write side swallows and logs.
//
// Privacy: the only free text is UsernameAttempted (failed logins only) and Details (a short,
// code-authored note). Passwords, tokens, headers and bodies are never stored, and identities are the
// users' GUID ids. Rows are built through the intent-named factories below so an event type can never
// carry fields it does not allow. occurredUtc is passed in so tests are deterministic.
public class UserActivityEvent
{
    // bigint identity; the table is append-only.
    public long Id { get; private set; }

    public DateTime OccurredUtc { get; private set; }

    public UserActivityEventType EventType { get; private set; }

    // The authenticated actor. Null for LoginFailed, where the caller is unproven.
    public Guid? ActorUserId { get; private set; }

    // The user acted UPON (role change / deletion target). Null for events with no distinct target.
    public Guid? TargetUserId { get; private set; }

    // Failed-login only: the attempted username. Never a password; null for every other event type.
    public string? UsernameAttempted { get; private set; }

    // Short, code-authored context (e.g. "role -> Admin"). Never a request body, header, email or secret.
    public string? Details { get; private set; }

    // nvarchar column caps shared by the factories.
    private const int UsernameAttemptedMaxLength = 100;
    private const int DetailsMaxLength = 500;

    private UserActivityEvent() { }

    public static UserActivityEvent LoginSucceeded(Guid actorUserId, DateTime occurredUtc) => new()
    {
        EventType = UserActivityEventType.LoginSucceeded,
        OccurredUtc = occurredUtc,
        ActorUserId = actorUserId
    };

    // No ActorUserId (the caller is unproven); a blank attempted username stores null.
    public static UserActivityEvent LoginFailed(string? usernameAttempted, DateTime occurredUtc) => new()
    {
        EventType = UserActivityEventType.LoginFailed,
        OccurredUtc = occurredUtc,
        UsernameAttempted = Cap(usernameAttempted, UsernameAttemptedMaxLength)
    };

    public static UserActivityEvent UserRegistered(Guid actorUserId, DateTime occurredUtc) => new()
    {
        EventType = UserActivityEventType.UserRegistered,
        OccurredUtc = occurredUtc,
        ActorUserId = actorUserId
    };

    // Same event type as a self-registration, told apart by carrying a TargetUserId (the new account)
    // while the actor is the admin who created it.
    public static UserActivityEvent UserCreatedByAdmin(
        Guid actingAdminId, Guid newUserId, DateTime occurredUtc) => new()
    {
        EventType = UserActivityEventType.UserRegistered,
        OccurredUtc = occurredUtc,
        ActorUserId = actingAdminId,
        TargetUserId = newUserId,
        Details = Cap("created by admin", DetailsMaxLength)
    };

    public static UserActivityEvent RoleChanged(
        Guid actorUserId, Guid targetUserId, string? details, DateTime occurredUtc) => new()
    {
        EventType = UserActivityEventType.RoleChanged,
        OccurredUtc = occurredUtc,
        ActorUserId = actorUserId,
        TargetUserId = targetUserId,
        Details = Cap(details, DetailsMaxLength)
    };

    public static UserActivityEvent UserDeleted(
        Guid actorUserId, Guid targetUserId, DateTime occurredUtc) => new()
    {
        EventType = UserActivityEventType.UserDeleted,
        OccurredUtc = occurredUtc,
        ActorUserId = actorUserId,
        TargetUserId = targetUserId
    };

    // Content mutations: ActorUserId is the acting admin stamped from the JWT (never the request body),
    // TargetUserId is null, and the content's short identifier travels in Details.

    public static UserActivityEvent PolicyFlagChanged(
        Guid actorUserId, string? details, DateTime occurredUtc) =>
        ContentChanged(UserActivityEventType.PolicyFlagChanged, actorUserId, details, occurredUtc);

    public static UserActivityEvent FestivalChanged(
        Guid actorUserId, string? details, DateTime occurredUtc) =>
        ContentChanged(UserActivityEventType.FestivalChanged, actorUserId, details, occurredUtc);

    public static UserActivityEvent NewsEventChanged(
        Guid actorUserId, string? details, DateTime occurredUtc) =>
        ContentChanged(UserActivityEventType.NewsEventChanged, actorUserId, details, occurredUtc);

    public static UserActivityEvent CropChanged(
        Guid actorUserId, string? details, DateTime occurredUtc) =>
        ContentChanged(UserActivityEventType.CropChanged, actorUserId, details, occurredUtc);

    public static UserActivityEvent MarketChanged(
        Guid actorUserId, string? details, DateTime occurredUtc) =>
        ContentChanged(UserActivityEventType.MarketChanged, actorUserId, details, occurredUtc);

    // Pipeline-control actions: an admin started, or asked to stop, the ingestion service. Same shape as a
    // content event (acting admin, no target, a short Details note) — here the note is the pass's batchId,
    // so a control action can be joined to the IngestionRuns rows it produced.

    public static UserActivityEvent IngestionServiceStarted(
        Guid actorUserId, string? details, DateTime occurredUtc) =>
        ContentChanged(UserActivityEventType.IngestionServiceStarted, actorUserId, details, occurredUtc);

    public static UserActivityEvent IngestionServiceStopRequested(
        Guid actorUserId, string? details, DateTime occurredUtc) =>
        ContentChanged(UserActivityEventType.IngestionServiceStopRequested, actorUserId, details, occurredUtc);

    // A FARMER cleared the planting date of a watched crop. Same row shape as a content event (actor, no
    // target, a short Details note) but the actor is the farmer, not an admin — this is the first event a
    // non-admin authors. Details is "<CropCode> · <Reason word>", rendered by
    // PlantedDateRemovalReasons.RenderAuditDetails; the farmer's free-text note is NEVER passed here, it
    // lives only on the PlantedDateRemovals row that the same request wrote inside its transaction.
    public static UserActivityEvent PlantedDateRemoved(
        Guid actorUserId, string? details, DateTime occurredUtc) =>
        ContentChanged(UserActivityEventType.PlantedDateRemoved, actorUserId, details, occurredUtc);

    // The farmer's SALES LOG: one factory per verb, because "a sale was deleted" is the fact an admin needs
    // to see in the list without opening the row. Details is "<CropCode> · Rs <price>/kg · <date>", rendered
    // by SaleAuditDetails, whose signature cannot accept the farmer's free-text note — the note lives only
    // on the UserSales row.

    public static UserActivityEvent SaleRecorded(
        Guid actorUserId, string? details, DateTime occurredUtc) =>
        ContentChanged(UserActivityEventType.SaleRecorded, actorUserId, details, occurredUtc);

    public static UserActivityEvent SaleUpdated(
        Guid actorUserId, string? details, DateTime occurredUtc) =>
        ContentChanged(UserActivityEventType.SaleUpdated, actorUserId, details, occurredUtc);

    public static UserActivityEvent SaleDeleted(
        Guid actorUserId, string? details, DateTime occurredUtc) =>
        ContentChanged(UserActivityEventType.SaleDeleted, actorUserId, details, occurredUtc);

    // Private so a content or pipeline row can only be built through the intent-named factories above.
    // An empty actor id is stored as NULL rather than an all-zeros GUID, so an action that reached the
    // handler without a JWT-stamped admin is recorded honestly instead of looking like a real account.
    private static UserActivityEvent ContentChanged(
        UserActivityEventType type, Guid actorUserId, string? details, DateTime occurredUtc) => new()
    {
        EventType = type,
        OccurredUtc = occurredUtc,
        ActorUserId = actorUserId == Guid.Empty ? null : actorUserId,
        Details = Cap(details, DetailsMaxLength)
    };

    // Trim then cap to the column length; a blank value stores null rather than an empty string.
    private static string? Cap(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
