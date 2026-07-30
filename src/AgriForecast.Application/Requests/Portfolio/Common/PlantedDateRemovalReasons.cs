using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Portfolio.Common;

/// <summary>
/// <see cref="PlantedDateRemovalReason"/> &lt;-&gt; wire-string mapping for the <c>clearReason</c> field of
/// PUT /api/portfolio/watchlist/{cropId}, plus the one place the audit note is worded. The wire strings are
/// camelCase and frozen here, so JSON never carries an enum int.
/// </summary>
/// <remarks>
/// CASE-SENSITIVE, deliberately unlike
/// <see cref="Admin.Logs.Common.UserActivityEventStrings"/>. That one tolerates casing because an admin can
/// hand-type a <c>?types=</c> query in a URL bar; this one is written by our own client into a request body,
/// where a differently-cased value means the caller is guessing at the contract. "cropfailed" is answered
/// with <c>invalid_clear_reason</c> rather than quietly accepted, because a reason picker that accepts
/// unspecified spellings is a contract that has stopped being one.
/// <para>
/// Adding a reason is exactly one <c>Pairs</c> row plus one <c>Words</c> row — the same
/// derive-everything-from-one-table shape UserActivityEventStrings uses, so a new reason cannot be
/// parseable but unrenderable (or vice versa).
/// </para>
/// </remarks>
public static class PlantedDateRemovalReasons
{
    public const string Harvested = "harvested";
    public const string CropFailed = "cropFailed";
    public const string EnteredByMistake = "enteredByMistake";
    public const string Other = "other";

    // Ordered so KnownReasons (and therefore any error message built from it) reads in enum order.
    private static readonly (PlantedDateRemovalReason Reason, string Wire)[] Pairs =
    {
        (PlantedDateRemovalReason.Harvested, Harvested),
        (PlantedDateRemovalReason.CropFailed, CropFailed),
        (PlantedDateRemovalReason.EnteredByMistake, EnteredByMistake),
        (PlantedDateRemovalReason.Other, Other)
    };

    // The admin-facing wording of each reason, used ONLY in the UserActivityLog.Details note. Code-authored
    // English: the farmer-facing labels are the UI's own translated copy and never travel over this wire.
    private static readonly Dictionary<PlantedDateRemovalReason, string> Words = new()
    {
        [PlantedDateRemovalReason.Harvested] = "Harvested",
        [PlantedDateRemovalReason.CropFailed] = "Crop failed",
        [PlantedDateRemovalReason.EnteredByMistake] = "Entered by mistake",
        [PlantedDateRemovalReason.Other] = "Other"
    };

    private static readonly Dictionary<PlantedDateRemovalReason, string> ByReason =
        Pairs.ToDictionary(p => p.Reason, p => p.Wire);

    // Ordinal (case-SENSITIVE) — see the remarks above.
    private static readonly Dictionary<string, PlantedDateRemovalReason> ByWire =
        Pairs.ToDictionary(p => p.Wire, p => p.Reason, StringComparer.Ordinal);

    /// <summary>Every accepted <c>clearReason</c> value, in enum order.</summary>
    public static readonly IReadOnlyCollection<string> KnownReasons =
        Pairs.Select(p => p.Wire).ToArray();

    /// <summary>The wire spelling of a reason. Total over the enum by construction.</summary>
    public static string ToWire(PlantedDateRemovalReason reason) =>
        ByReason.TryGetValue(reason, out var wire)
            ? wire
            // Defensive default: camelCase the enum name so an un-mapped future member never emits an int.
            : char.ToLowerInvariant(reason.ToString()[0]) + reason.ToString()[1..];

    /// <summary>
    /// Parses a <c>clearReason</c> value, or null when it is blank or not an exact known spelling.
    /// Surrounding whitespace is tolerated; different casing is NOT.
    /// </summary>
    public static PlantedDateRemovalReason? TryParse(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return null;
        return ByWire.TryGetValue(reason.Trim(), out var parsed) ? parsed : null;
    }

    /// <summary>
    /// The UserActivityLog.Details note for a removal: <c>"&lt;CropCode&gt; · &lt;Reason word&gt;"</c>, e.g.
    /// <c>"VEG000019 · Harvested"</c>. A blank crop code renders the reason word alone rather than
    /// " · Harvested".
    /// </summary>
    /// <remarks>
    /// THE FARMER'S NOTE IS NOT A PARAMETER, and that is the enforcement. Audit Details is code-authored text
    /// only; the free-text note lives solely in PlantedDateRemovals.Note, where it is scoped to its owner.
    /// A signature that could not accept the note cannot leak it by a careless edit at a call site.
    /// </remarks>
    public static string RenderAuditDetails(string? cropCode, PlantedDateRemovalReason reason)
    {
        var word = Words.TryGetValue(reason, out var w) ? w : reason.ToString();

        return string.IsNullOrWhiteSpace(cropCode) ? word : $"{cropCode.Trim()} · {word}";
    }
}
