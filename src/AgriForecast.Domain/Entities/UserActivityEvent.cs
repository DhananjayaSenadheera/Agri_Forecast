using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// One row per security-relevant account event -> table UserActivityLog (Logs hub PR A). WRITTEN by
// the .NET side via IUserActivityAudit at the five existing auth/user-management call sites; READ by
// the admin Logs hub (AdminLogsController). An audit failure must NEVER break the operation being
// audited, so the write side (UserActivityAudit) isolates every insert in its own service scope and
// swallows-and-logs — this entity just guarantees a row can never be built into an inconsistent or
// leaky state.
//
// PRIVACY: the only free-text captured is UsernameAttempted (failed logins only — the attempted
// username IS the security signal) and Details (a short, code-authored note like "role -> Admin").
// A PASSWORD/TOKEN/HEADER/BODY is NEVER stored; both text columns are hard-capped to their column
// length. Identities are the users' immutable GUID ids, never emails or names.
//
// Setters are private and rows are built via the intent-named factories below (house style, as
// IngestionRun.StartRunning) so a row can never be poked into a state its event type does not allow
// (e.g. a LoginFailed row never carries an ActorUserId; a LoginSucceeded row never carries an
// attempted-username string). occurredUtc is passed in (never an internal UtcNow) so the caller
// controls the clock and tests are deterministic.
public class UserActivityEvent
{
    // bigint identity (DB-generated) — this table is append-only and can grow large.
    public long Id { get; private set; }

    public DateTime OccurredUtc { get; private set; }

    public UserActivityEventType EventType { get; private set; }

    // The authenticated actor (the user who logged in / registered, or the acting admin). Null for a
    // LoginFailed event, where the caller is unproven.
    public Guid? ActorUserId { get; private set; }

    // The user acted UPON (role change / deletion target). Null for events with no distinct target.
    public Guid? TargetUserId { get; private set; }

    // Failed-login only: the ATTEMPTED username (the security signal). Capped to the column length.
    // NEVER a password. Null for every other event type.
    public string? UsernameAttempted { get; private set; }

    // Short, code-authored context (e.g. "role -> Admin"). Capped to the column length. NEVER a
    // request body, header, email, or secret.
    public string? Details { get; private set; }

    // nvarchar column caps shared by the factories.
    private const int UsernameAttemptedMaxLength = 100;
    private const int DetailsMaxLength = 500;

    private UserActivityEvent() { }

    // ── Intent-named factories: one per event type, each admitting only that event's valid fields ──

    public static UserActivityEvent LoginSucceeded(Guid actorUserId, DateTime occurredUtc) => new()
    {
        EventType = UserActivityEventType.LoginSucceeded,
        OccurredUtc = occurredUtc,
        ActorUserId = actorUserId
    };

    // No ActorUserId (the caller is unproven). The attempted username is captured (trimmed + capped);
    // an empty/blank attempt stores null.
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

    // An admin provisioned the account instead of the user signing themselves up. Same event type as
    // a self-registration (the read side's filter set stays closed at five), told apart by carrying a
    // TargetUserId — the new account — while the actor is the admin who created it. A
    // self-registration never has a target, so the two can never be confused.
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

    // Trim then hard-cap to the column length; a blank/empty value stores null (never a fabricated
    // empty string). Guards against a column-overflow write turning an audit save into a failure.
    private static string? Cap(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        return trimmed.Length > maxLength ? trimmed[..maxLength] : trimmed;
    }
}
