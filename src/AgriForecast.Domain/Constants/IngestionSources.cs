namespace AgriForecast.Domain.Constants;

/// <summary>
/// The source keys written to IngestionRuns.Source and IngestionWatermark.Source, and the values
/// the admin ?source= filter accepts. Exact strings — the casing here is the stored casing, though
/// membership is compared case-insensitively. Adding a source is an entry here plus a Worker call site.
/// The ?source= filter deliberately accepts this FULL run-source set, not just the watermark-backed
/// subset (HARTI/CBSL/CBSL_MACRO) — narrowing the validator would wrongly 400 valid filters like
/// DAMBULLA_DEC or WEATHER, whose only health signal is their run rows.
/// </summary>
public static class IngestionSources
{
    public const string DambullaDec = "DAMBULLA_DEC";
    public const string Weather = "WEATHER";
    public const string Economic = "ECONOMIC";
    public const string News = "NEWS";
    public const string Harti = "HARTI";
    public const string Cbsl = "CBSL";
    public const string CbslMacro = "CBSL_MACRO";

    /// <summary>
    /// Run row for the Python feature-build step. It is a real run row (listed and filterable), but it
    /// is not an ingestion source, so it is excluded from the ingestion status card.
    /// </summary>
    public const string FeatureBuild = "FEATURE_BUILD";

    /// <summary>Every source key that can appear in <c>IngestionRuns.Source</c>.</summary>
    public static readonly IReadOnlyCollection<string> KnownKeys = new[]
    {
        DambullaDec, Weather, Economic, News, Harti, Cbsl, CbslMacro, FeatureBuild
    };

    /// <summary>
    /// Run sources that must not affect the admin status card's state, lastRunAtUtc or lastRunStatus.
    /// The feature build runs last every day, so without this exclusion it would permanently be the
    /// latest run and a failed DAMBULLA_DEC / WEATHER / ECONOMIC / NEWS could never show up there.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ExcludedFromServiceState = new[]
    {
        FeatureBuild
    };

    /// <summary>True if <paramref name="source"/> is a known ingestion source (case-insensitive).</summary>
    public static bool IsKnown(string? source) =>
        source is not null &&
        KnownKeys.Any(k => string.Equals(k, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns the stored casing for a known source, or the trimmed input if unknown.</summary>
    public static string? Canonicalize(string? source)
    {
        if (source is null) return null;
        var trimmed = source.Trim();
        return KnownKeys.FirstOrDefault(k => string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase))
               ?? trimmed;
    }
}
