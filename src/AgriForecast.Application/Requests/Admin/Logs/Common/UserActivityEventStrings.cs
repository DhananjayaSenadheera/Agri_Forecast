using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Admin.Logs.Common;

// Central UserActivityEventType <-> wire-string mapping for the admin Logs DTOs + the ?type= /
// ?types= filters, so the exact strings the FE consumes (and filters by) live in ONE place (mirrors
// IngestionStatusStrings). Deliberate casing per the Logs-hub contract: LOWERCASE camelCase. The DTO
// exposes a plain string (never the enum) so System.Text.Json never emits an int and the wire values
// are frozen here.
//
// SINGLE SOURCE OF TRUTH: ToWire, KnownTypes and TryParse are all derived from the ordered Pairs
// table below. Previously each was hand-maintained, so adding an event type and forgetting KnownTypes
// silently produced a type that could be READ but never FILTERED (its own wire string would 400).
// Adding a member is now exactly one Pairs row; EventStrings_MappingCoversEveryEnumMember pins that
// the table stays total.
public static class UserActivityEventStrings
{
    // Account events (Logs hub PR A) — the original five.
    public const string LoginSucceeded = "loginSucceeded";
    public const string LoginFailed = "loginFailed";
    public const string UserRegistered = "userRegistered";
    public const string RoleChanged = "roleChanged";
    public const string UserDeleted = "userDeleted";

    // Admin CONTENT events — one per mutable entity kind (create/update/delete share the type and are
    // told apart by Details). Appended, never reordered: the enum's ints are persisted.
    public const string PolicyFlagChanged = "policyFlagChanged";
    public const string FestivalChanged = "festivalChanged";
    public const string NewsEventChanged = "newsEventChanged";
    public const string CropChanged = "cropChanged";
    public const string MarketChanged = "marketChanged";

    // Ordered so KnownTypes (and therefore the validator's error message) reads in enum order.
    private static readonly (UserActivityEventType Type, string Wire)[] Pairs =
    {
        (UserActivityEventType.LoginSucceeded, LoginSucceeded),
        (UserActivityEventType.LoginFailed, LoginFailed),
        (UserActivityEventType.UserRegistered, UserRegistered),
        (UserActivityEventType.RoleChanged, RoleChanged),
        (UserActivityEventType.UserDeleted, UserDeleted),
        (UserActivityEventType.PolicyFlagChanged, PolicyFlagChanged),
        (UserActivityEventType.FestivalChanged, FestivalChanged),
        (UserActivityEventType.NewsEventChanged, NewsEventChanged),
        (UserActivityEventType.CropChanged, CropChanged),
        (UserActivityEventType.MarketChanged, MarketChanged)
    };

    private static readonly Dictionary<UserActivityEventType, string> ByType =
        Pairs.ToDictionary(p => p.Type, p => p.Wire);

    private static readonly Dictionary<string, UserActivityEventType> ByWire =
        Pairs.ToDictionary(p => p.Wire, p => p.Type, StringComparer.OrdinalIgnoreCase);

    public static string ToWire(UserActivityEventType t) =>
        ByType.TryGetValue(t, out var wire)
            ? wire
            // Defensive default: camelCase the enum name so an un-mapped future member never emits an int.
            : char.ToLowerInvariant(t.ToString()[0]) + t.ToString()[1..];

    // The full set of valid ?type= / ?types= filter values (validator message + membership check).
    public static readonly IReadOnlyCollection<string> KnownTypes =
        Pairs.Select(p => p.Wire).ToArray();

    // True if the wire string is a known event type (case-insensitive so a lower/upper query is
    // tolerated before it 400s on a genuine typo).
    public static bool IsKnown(string? type) =>
        type is not null && ByWire.ContainsKey(type.Trim());

    // Parse a wire string to the enum, or null when blank/unknown. Case-insensitive.
    public static UserActivityEventType? TryParse(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        return ByWire.TryGetValue(type.Trim(), out var parsed) ? parsed : null;
    }

    // Splits a comma-separated ?types= list into trimmed, non-blank tokens (order + duplicates
    // preserved — the validator reports on the raw tokens, the handler de-duplicates). A null/blank
    // list yields an empty array so "absent" and "blank" behave identically (= no types filter).
    public static IReadOnlyList<string> SplitTypes(string? types) =>
        string.IsNullOrWhiteSpace(types)
            ? Array.Empty<string>()
            : types.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
