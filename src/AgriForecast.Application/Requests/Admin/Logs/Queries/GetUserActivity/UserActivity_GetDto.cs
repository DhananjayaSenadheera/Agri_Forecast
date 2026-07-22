namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;

// One account event for GET /api/admin/logs/user-activity. eventType is a pre-rendered frozen
// lowercase wire string (never the enum, so JSON never emits an int). actorUserId/targetUserId are
// the users' immutable GUID ids (nullable — a failed login has no proven actor, a login has no
// target). usernameAttempted is populated for failed logins only (the security signal, never a
// password); details is a short code-authored note (e.g. "role -> Admin"). PascalCase -> camelCase
// JSON per the API default policy.
public class UserActivity_GetDto
{
    public DateTime OccurredUtc { get; set; }
    public string EventType { get; set; } = string.Empty; // loginSucceeded|loginFailed|userRegistered|roleChanged|userDeleted
    public Guid? ActorUserId { get; set; }
    public Guid? TargetUserId { get; set; }
    public string? UsernameAttempted { get; set; }
    public string? Details { get; set; }
}
