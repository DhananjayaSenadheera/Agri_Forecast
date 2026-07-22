using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Admin.Logs.Common;

// Central UserActivityEventType <-> wire-string mapping for the admin Logs DTOs + the ?type= filter,
// so the exact strings the FE consumes (and filters by) live in ONE place (mirrors
// IngestionStatusStrings). Deliberate casing per the Logs-hub contract: LOWERCASE camelCase
// (loginSucceeded|loginFailed|userRegistered|roleChanged|userDeleted). The DTO exposes a plain string
// (never the enum) so System.Text.Json never emits an int and the wire values are frozen here.
public static class UserActivityEventStrings
{
    public const string LoginSucceeded = "loginSucceeded";
    public const string LoginFailed = "loginFailed";
    public const string UserRegistered = "userRegistered";
    public const string RoleChanged = "roleChanged";
    public const string UserDeleted = "userDeleted";

    public static string ToWire(UserActivityEventType t) => t switch
    {
        UserActivityEventType.LoginSucceeded => LoginSucceeded,
        UserActivityEventType.LoginFailed => LoginFailed,
        UserActivityEventType.UserRegistered => UserRegistered,
        UserActivityEventType.RoleChanged => RoleChanged,
        UserActivityEventType.UserDeleted => UserDeleted,
        // Defensive default: camelCase the enum name so an un-mapped future member never emits an int.
        _ => char.ToLowerInvariant(t.ToString()[0]) + t.ToString()[1..]
    };

    // The full set of valid ?type= filter values (for the validator's message + membership check).
    public static readonly IReadOnlyCollection<string> KnownTypes = new[]
    {
        LoginSucceeded, LoginFailed, UserRegistered, RoleChanged, UserDeleted
    };

    // True if the wire string is a known event type (case-insensitive so a lower/upper query is
    // tolerated before it 400s on a genuine typo).
    public static bool IsKnown(string? type) =>
        type is not null &&
        KnownTypes.Any(k => string.Equals(k, type.Trim(), StringComparison.OrdinalIgnoreCase));

    // Parse a wire string to the enum, or null when blank/unknown. Case-insensitive.
    public static UserActivityEventType? TryParse(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var trimmed = type.Trim();
        return trimmed switch
        {
            _ when string.Equals(trimmed, LoginSucceeded, StringComparison.OrdinalIgnoreCase) => UserActivityEventType.LoginSucceeded,
            _ when string.Equals(trimmed, LoginFailed, StringComparison.OrdinalIgnoreCase) => UserActivityEventType.LoginFailed,
            _ when string.Equals(trimmed, UserRegistered, StringComparison.OrdinalIgnoreCase) => UserActivityEventType.UserRegistered,
            _ when string.Equals(trimmed, RoleChanged, StringComparison.OrdinalIgnoreCase) => UserActivityEventType.RoleChanged,
            _ when string.Equals(trimmed, UserDeleted, StringComparison.OrdinalIgnoreCase) => UserActivityEventType.UserDeleted,
            _ => null
        };
    }
}
