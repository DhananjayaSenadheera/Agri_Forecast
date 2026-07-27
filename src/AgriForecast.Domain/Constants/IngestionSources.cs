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

    /// <summary>
    /// Run row for the nightly forecast-snapshot trigger (farmer-portfolio PRD 4.2). Written by
    /// trigger_forecast_snapshot.py (Python, agriforecast_ml/snapshot_run_log.py), invoked from the
    /// daily pipeline's build-features container AFTER build_features.py — not by any .NET ingestion
    /// service (the original ".NET Worker, last in its pass" design was corrected 2026-07-27: the
    /// snapshot/mature passes read CropFeatureDaily, which is only current once the feature build has
    /// run). This key exists here purely so the admin runs list/filters recognise it — it is a real run
    /// row (listed and filterable), not an ingestion source.
    /// </summary>
    public const string ForecastSnapshot = "FORECAST_SNAPSHOT";

    /// <summary>Every source key that can appear in <c>IngestionRuns.Source</c>.</summary>
    public static readonly IReadOnlyCollection<string> KnownKeys = new[]
    {
        DambullaDec, Weather, Economic, News, Harti, Cbsl, CbslMacro, FeatureBuild, ForecastSnapshot
    };

    /// <summary>
    /// Run sources that must not affect the admin status card's state, lastRunAtUtc or lastRunStatus,
    /// the pipeline health banner/sentinel, or the running/stopped detection start/stop rely on
    /// (IsRunningPerRunRowsAsync). Both members here run as standalone Python steps in the build-features
    /// container, each minting its own solo BatchId — they never join the ingestion Worker's shared batch,
    /// so their outcome must never stand in for "did the Worker's pass succeed". The feature build runs
    /// last every day, so without this exclusion it would permanently be the latest run and a failed
    /// DAMBULLA_DEC / WEATHER / ECONOMIC / NEWS could never show up there. FORECAST_SNAPSHOT runs even
    /// later (after FEATURE_BUILD): without excluding it too, a Failed snapshot row would flip the
    /// admin-wide pipeline banner red and fire the sentinel email over a report-only pass (violates PRD
    /// farmer-portfolio-and-forecast-snapshots.md §3.7 — the snapshot job must never gate anything), and a
    /// Succeeded snapshot row — being the latest solo batch in the window — would win batch selection and
    /// paper over a genuinely partial ingestion night as green (the exact silent-failure class the health
    /// endpoint exists to catch). A Running snapshot row would also be misread by
    /// IsRunningPerRunRowsAsync as "ingestion still running", blocking Start/Stop.
    /// </summary>
    public static readonly IReadOnlyCollection<string> ExcludedFromServiceState = new[]
    {
        FeatureBuild, ForecastSnapshot
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
