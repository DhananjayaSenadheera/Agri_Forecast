namespace AgriForecast.Domain.Constants;

/// <summary>
/// The values stored in ForecastSnapshots.MaturityState. Deliberately STRINGS, not the usual
/// enum-as-int of this codebase: the Python snapshot/mature job writes and filters these rows with
/// raw SQL, and the filtered index on the table is literally WHERE MaturityState = 'pending'. An int
/// enum would force a magic-number mirror in Python and make the index unreadable. The exact lowercase
/// spellings below are the wire+storage contract shared with the ML service — never re-case them.
/// </summary>
public static class ForecastSnapshotMaturityStates
{
    /// <summary>Awaiting its harvest date; the maturing sweep scans exactly these rows.</summary>
    public const string Pending = "pending";

    /// <summary>Terminal: an actual price was found and the error columns are filled.</summary>
    public const string Matured = "matured";

    /// <summary>
    /// Terminal: the harvest date passed but no actual price could be matched inside the allowed
    /// carry-back window. Counted and surfaced, never silently dropped and never faked.
    /// </summary>
    public const string ActualUnavailable = "actual_unavailable";

    /// <summary>
    /// Terminal FROM CREATION: the crop had no resolvable growth period, so there is no harvest date
    /// to mature against. Recorded for completeness and excluded from accuracy aggregates.
    /// </summary>
    public const string NotMaturable = "not_maturable";

    /// <summary>Every value that can appear in <c>ForecastSnapshots.MaturityState</c>.</summary>
    public static readonly IReadOnlyCollection<string> All = new[]
    {
        Pending, Matured, ActualUnavailable, NotMaturable
    };
}
