namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;

// One event for GET /api/admin/logs/user-activity. eventType is a frozen lowercase wire string, so JSON
// never emits an enum int. actorUserId/targetUserId are nullable (a failed login has no proven actor, a
// login has no target). usernameAttempted is set for failed logins only and is never a password.
public class UserActivity_GetDto
{
    public DateTime OccurredUtc { get; set; }
    public string EventType { get; set; } = string.Empty; // loginSucceeded|loginFailed|userRegistered|roleChanged|userDeleted
    public Guid? ActorUserId { get; set; }
    public Guid? TargetUserId { get; set; }
    public string? UsernameAttempted { get; set; }
    public string? Details { get; set; }
}
