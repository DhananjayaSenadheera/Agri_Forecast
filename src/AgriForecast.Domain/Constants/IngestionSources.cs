namespace AgriForecast.Domain.Constants;

/// <summary>
/// The closed set of ingestion SOURCE keys the Worker emits — one <c>IngestionRun</c> row per
/// source per pass, and (for the resumable sources) one <c>IngestionWatermark</c> row. These string
/// keys are the single source of truth shared by the run/watermark rows and the admin read surface
/// (the status "sources" list + the runs <c>?source=</c> filter validator).
///
/// The three resumable sources (HARTI, CBSL, CBSL_MACRO) also carry watermark rows; the status-only
/// sources (DAMBULLA_DEC, WEATHER, ECONOMIC, NEWS) do not. The runs <c>?source=</c> filter targets
/// <c>IngestionRuns.Source</c>, so the valid filter set is the FULL run-source set below (NOT just
/// the watermark-backed subset) — restricting it to watermark sources would wrongly 400 a valid
/// DAMBULLA_DEC / WEATHER filter. (The PR-3 spec said "known watermark source keys"; this is the
/// deliberate reconciliation, since the filter is on run rows, not watermark rows.)
///
/// STRING keys (not an enum) match the <c>Source</c> columns, which are plain strings — adding a
/// source is a new entry here + a Worker call site, no migration. This mirrors the values wired in
/// the Worker + the per-service <c>SourceKey</c> consts (which live in Infrastructure and so cannot
/// be referenced from the Application-layer validator; this Domain constant is the shared home).
/// Membership is compared case-insensitively so a lower/upper-case query cannot bypass validation
/// (the SQL Server default collation is case-insensitive anyway).
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
    /// The Python feature-build step's run row. NOT an ingestion source: nothing is fetched from an
    /// external provider — it records that <c>build_features</c> ran, reusing the IngestionRuns table
    /// rather than adding a near-identical one. It IS a legitimate run row (listed by GET /runs and
    /// filterable by <c>?source=FEATURE_BUILD</c>, which is why it belongs in
    /// <see cref="KnownKeys"/>), but it is EXCLUDED from the ingestion-service state derivation — see
    /// <see cref="ExcludedFromServiceState"/>.
    /// </summary>
    public const string FeatureBuild = "FEATURE_BUILD";

    /// <summary>Every source key that can appear in <c>IngestionRuns.Source</c>.</summary>
    public static readonly IReadOnlyCollection<string> KnownKeys = new[]
    {
        DambullaDec, Weather, Economic, News, Harti, Cbsl, CbslMacro, FeatureBuild
    };

    /// <summary>
    /// Run sources that must NOT influence the admin ingestion-status card's <c>state</c>,
    /// <c>lastRunAtUtc</c> or <c>lastRunStatus</c>, because they do not describe the ingestion
    /// service. Today: FEATURE_BUILD.
    /// <para>
    /// Why it is load-bearing: the feature build runs LAST every day as a solo one-row batch, so
    /// without this exclusion the "latest run" is permanently the feature build, its batch rolls up
    /// to "succeeded" forever, and the FE red-dot alarm (<c>lastRunStatus === 'failed'</c>) could
    /// never fire for DAMBULLA_DEC / WEATHER / ECONOMIC / NEWS — precisely the four sources that have
    /// no watermark row and are therefore visible ONLY through that card. A hung feature build would
    /// likewise report the ingestion service as "running".
    /// </para>
    /// The POLICY lives here and is passed in by GetIngestionStatusQueryHandler; the read store only
    /// applies the exclusion it is handed, so there is one place to change and it is unit-testable.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ExcludedFromServiceState = new[]
    {
        FeatureBuild
    };

    /// <summary>True if <paramref name="source"/> is a known ingestion source (case-insensitive).</summary>
    public static bool IsKnown(string? source) =>
        source is not null &&
        KnownKeys.Any(k => string.Equals(k, source, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the canonical (stored) casing for a known source, or the input trimmed if unknown.
    /// Lets a case-insensitive query normalize to the exact stored key before hitting the DB.
    /// </summary>
    public static string? Canonicalize(string? source)
    {
        if (source is null) return null;
        var trimmed = source.Trim();
        return KnownKeys.FirstOrDefault(k => string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase))
               ?? trimmed;
    }
}
