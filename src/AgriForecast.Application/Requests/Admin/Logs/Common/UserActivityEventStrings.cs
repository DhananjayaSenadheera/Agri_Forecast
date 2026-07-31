using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Admin.Logs.Common;

// UserActivityEventType <-> wire-string mapping for the admin Logs DTOs and the ?type= / ?types= filters.
// The wire strings are lowercase camelCase and frozen here, so JSON never emits an enum int.
// ToWire, KnownTypes and TryParse are all derived from the Pairs table below, so adding an event type is
// exactly one Pairs row — previously a new type could be readable but not filterable.
public static class UserActivityEventStrings
{
    // Account events (Logs hub PR A) — the original five.
    public const string LoginSucceeded = "loginSucceeded";
    public const string LoginFailed = "loginFailed";
    public const string UserRegistered = "userRegistered";
    public const string RoleChanged = "roleChanged";
    public const string UserDeleted = "userDeleted";

    // Content events, one per mutable entity kind. Appended, never reordered: the enum ints are persisted.
    public const string PolicyFlagChanged = "policyFlagChanged";
    public const string FestivalChanged = "festivalChanged";
    public const string NewsEventChanged = "newsEventChanged";
    public const string CropChanged = "cropChanged";
    public const string MarketChanged = "marketChanged";

    // Pipeline-control events: an admin used the ingestion start/stop control.
    public const string IngestionServiceStarted = "ingestionServiceStarted";
    public const string IngestionServiceStopRequested = "ingestionServiceStopRequested";

    // The first FARMER-authored event: a farmer cleared the planting date of a watched crop. Details is
    // "<CropCode> · <Reason word>"; the farmer's optional free-text note is deliberately NOT in it.
    public const string PlantedDateRemoved = "plantedDateRemoved";

    // The farmer's sales log — one wire string PER VERB, so an admin can filter the Logs page for deletions
    // alone. Details is "<CropCode> · Rs <price>/kg · <date>"; the farmer's optional free-text note is
    // deliberately NOT in it.
    public const string SaleRecorded = "saleRecorded";
    public const string SaleUpdated = "saleUpdated";
    public const string SaleDeleted = "saleDeleted";

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
        (UserActivityEventType.MarketChanged, MarketChanged),
        (UserActivityEventType.IngestionServiceStarted, IngestionServiceStarted),
        (UserActivityEventType.IngestionServiceStopRequested, IngestionServiceStopRequested),
        (UserActivityEventType.PlantedDateRemoved, PlantedDateRemoved),
        (UserActivityEventType.SaleRecorded, SaleRecorded),
        (UserActivityEventType.SaleUpdated, SaleUpdated),
        (UserActivityEventType.SaleDeleted, SaleDeleted)
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

    // Case-insensitive, so a lower/upper-case query is tolerated before a genuine typo 400s.
    public static bool IsKnown(string? type) =>
        type is not null && ByWire.ContainsKey(type.Trim());

    // Parse a wire string to the enum, or null when blank/unknown. Case-insensitive.
    public static UserActivityEventType? TryParse(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        return ByWire.TryGetValue(type.Trim(), out var parsed) ? parsed : null;
    }

    // Splits a comma-separated ?types= list into trimmed, non-blank tokens, preserving order and
    // duplicates. A null or blank list yields an empty array, so absent and blank behave identically.
    public static IReadOnlyList<string> SplitTypes(string? types) =>
        string.IsNullOrWhiteSpace(types)
            ? Array.Empty<string>()
            : types.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
