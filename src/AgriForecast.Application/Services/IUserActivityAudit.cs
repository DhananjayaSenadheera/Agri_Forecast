namespace AgriForecast.Application.Services;

// Fire-safe audit seam for security-relevant account events (Logs hub PR A). Called from the five
// auth/user-management handlers alongside their existing LogInformation calls; the implementation
// (UserActivityAudit) writes one UserActivityLog row per call in its OWN service scope and
// SWALLOWS-AND-LOGS any failure — an audit write must NEVER add a failure mode to login /
// registration / an admin op (so these are awaited-but-guarded, never able to throw into the caller).
//
// The methods take only primitives the call sites already hold (ids, the attempted username, the new
// role) — never a password, token, header, or request body. The implementation stamps the clock.
public interface IUserActivityAudit
{
    // Successful credential check + token issue. actorUserId = the user who logged in.
    Task RecordLoginSucceededAsync(Guid actorUserId, CancellationToken ct = default);

    // Failed credential check. usernameAttempted = the ATTEMPTED username (trimmed + capped by the
    // entity) — the security signal, NEVER the password. No actor (the caller is unproven).
    Task RecordLoginFailedAsync(string? usernameAttempted, CancellationToken ct = default);

    // A new account was self-registered. actorUserId = the new user's id.
    Task RecordUserRegisteredAsync(Guid actorUserId, CancellationToken ct = default);

    // An admin provisioned an account from the admin console. Shares the UserRegistered event type
    // (the wire set stays the frozen five the Logs hub filters on) but is told apart by its shape:
    // the ACTOR is the acting admin and the TARGET is the new user, where a self-registration has an
    // actor and no target. Without this the trail would credit an admin-created account to the new
    // user themselves and lose the admin who actually created it.
    Task RecordUserCreatedByAdminAsync(Guid actingAdminId, Guid newUserId, CancellationToken ct = default);

    // An admin changed a user's role. Details is rendered as "role -> <newRole>".
    Task RecordRoleChangedAsync(Guid actorUserId, Guid targetUserId, string newRole, CancellationToken ct = default);

    // An admin deleted a user.
    Task RecordUserDeletedAsync(Guid actorUserId, Guid targetUserId, CancellationToken ct = default);
}
