using System.Globalization;
using System.Text;
using AgriForecast.Application.Requests.Admin.Pipeline.Queries.GetPipelineHealth;
using AgriForecast.Application.Services;

namespace AgriForecast.API.Startup.Sentinel;

/// <summary>
/// Turns a pipeline-health snapshot into the plain-text email the owner reads at breakfast.
/// <para>The one-line explanation of each state uses the SAME words as the admin banner in the app, so
/// the mail and the screen tell one story: "did not run", "stopped with an error", "blocked at the
/// quality check", "ran partially". "Quality check" rather than "data verification" is deliberate — it
/// is the phrase the UI uses (en.json qualityCheck), and rewording one side without the other is how an
/// operator learns to distrust both.</para>
/// <para>Honesty rules baked in here: the state token is printed verbatim (never softened), the detail
/// fields are printed even when they are empty (an absent feature build is shown as "(none)", not
/// omitted so the mail looks tidier), and the green heartbeat says out loud that its own absence is a
/// symptom.</para>
/// </summary>
public static class PipelineSentinelEmails
{
    private const string Subject = "AgriForecast pipeline";

    public static SentinelEmail ComposeAlert(
        PipelineHealth_GetDto health, string logsUrl, TimeOnly checkTime, string zoneLabel) =>
        new(
            $"{Subject}: {health.State} for {health.ExpectedForDate}",
            new StringBuilder()
                .AppendLine("Last night's AgriForecast data pipeline did NOT finish cleanly.")
                .AppendLine()
                .AppendLine($"What happened: {Explain(health.State)}")
                .AppendLine()
                .Append(Details(health, zoneLabel))
                .AppendLine()
                .Append(Footer(logsUrl, checkTime, zoneLabel))
                .ToString());

    public static SentinelEmail ComposeHeartbeat(
        PipelineHealth_GetDto health, string logsUrl, TimeOnly checkTime, string zoneLabel) =>
        new(
            $"{Subject}: {health.State} for {health.ExpectedForDate}",
            new StringBuilder()
                .AppendLine("Last night's AgriForecast data pipeline ran, passed verification and rebuilt")
                .AppendLine("the feature store. Nothing to do.")
                .AppendLine()
                .Append(Details(health, zoneLabel))
                .AppendLine()
                .AppendLine("This is the nightly all-clear. If it stops arriving, either the pipeline or this")
                .AppendLine("sentinel is broken — treat the silence as a symptom, not as good news.")
                .AppendLine()
                .Append(Footer(logsUrl, checkTime, zoneLabel))
                .ToString());

    // The state -> plain-English map. Mirrors the admin banner copy.
    private static string Explain(string state) => state switch
    {
        PipelineHealthStates.Missing =>
            "the pipeline did not run — nothing started within the catch-up window that follows the " +
            "scheduled time. Nothing was ingested, so today's prices, weather and features are " +
            "yesterday's.",
        PipelineHealthStates.Failed =>
            "the pipeline stopped with an error — the day's data may be incomplete or unchecked.",
        PipelineHealthStates.GateBlocked =>
            "the pipeline was blocked at the quality check — bad data was kept out. The feature store " +
            "was deliberately not rebuilt from it.",
        PipelineHealthStates.Partial =>
            "the pipeline ran partially. Some sources landed and some did not, so the day's data is " +
            "incomplete.",
        // Reached only when a night was STILL running one re-check interval before the next scheduled
        // fire. That is not a terminal state, and pretending it resolved would be the exact lie this
        // sentinel exists to prevent.
        PipelineHealthStates.Running =>
            "the pipeline was still running when this check gave up waiting, shortly before the next " +
            "scheduled run. It never reached a finished state.",
        _ =>
            $"the pipeline reported an unrecognised state ('{state}'). Treat it as unverified until " +
            "someone looks."
    };

    private static string Details(PipelineHealth_GetDto health, string zoneLabel) =>
        new StringBuilder()
            .AppendLine("Details")
            .AppendLine($"  Night ({zoneLabel}):    {health.ExpectedForDate}")
            .AppendLine($"  State:                  {health.State}")
            .AppendLine($"  Quality check:          {Or(health.VerificationStatus)}")
            .AppendLine($"  Feature build:          {Or(health.FeatureBuildStatus)}")
            .AppendLine($"  Batch id:               {Or(health.BatchId?.ToString())}")
            .AppendLine($"  Started (UTC):          {Or(Utc(health.StartedUtc))}")
            .AppendLine($"  Checked at (UTC):       {Utc(health.CheckedAtUtc)}")
            .ToString();

    private static string Footer(string logsUrl, TimeOnly checkTime, string zoneLabel) =>
        new StringBuilder()
            .AppendLine("Ingestion log (what ran, what failed, what the gate said):")
            .AppendLine(logsUrl)
            .AppendLine()
            .AppendLine("--")
            .AppendLine("Automated message from the AgriForecast pipeline sentinel, which runs inside the API")
            .AppendLine($"and checks once a night at {checkTime:HH\\:mm} {zoneLabel}. Turn the all-clear off with")
            .AppendLine("Sentinel__SendGreenHeartbeat=false (alerts always send).")
            .ToString();

    // Empty fields are SHOWN as "(none)" rather than dropped: "no feature build row exists" is itself a
    // finding, and a mail that quietly omits the line reads as if the question was never asked.
    private static string Or(string? value) => string.IsNullOrWhiteSpace(value) ? "(none)" : value;

    private static string? Utc(DateTime? value) => value is null
        ? null
        : value.Value.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
