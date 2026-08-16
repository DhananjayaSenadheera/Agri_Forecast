using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgriForecast.Application.common;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.CbslMacroIngestion;

// CBSL macro (CCPI / MEI vintage) ingestion — a properly-wired skeleton that is DISABLED by default, not
// faked. Gating and lifecycle follow CbslPriceReportIngestionService; the HTTP seam follows
// HartiBulletinIngestionService (an authenticated X-API-Key POST to an /admin/* route on the ML service).
//
// The CBSL PDF fetch, parse and upsert is Python and stays Python (ingest_cbsl_macro.py). This service does
// not parse PDFs; it triggers the Python pass via POST /admin/ingest-cbsl-macro, then records per-series
// watermarks from the returned perSeriesCoverage.
//
// The flag MacroSources:CbslMacro:Enabled defaults FALSE. A Disabled source is a no-op and is never counted
// as a failure — a source we chose not to run must not pollute fail isolation or trip alerting. The gating
// watermark (Source = "CBSL_MACRO") is created or kept Disabled with the reason recorded, so ops can tell
// intentionally-off apart from failing.
//
// perSeriesCoverage is { "<SeriesCode>": <rowCount> }, so one IngestionWatermark is recorded PER SERIES,
// keyed "CBSL_MACRO_<SeriesCode>", and one late or absent series never masks another. These watermarks are
// coverage bookkeeping, NOT a resume lower bound: the Python pass resumes from its own DB watermark read
// off MacroSeriesPoints, so nothing here is ever sent back to it.
//
// To enable later, set MacroSources:CbslMacro:Enabled = true. A transport failure, a non-2xx, an unparseable
// body, or a 200 whose summary status is not "ok" are each recorded on the gating watermark without
// advancing anything, and return Failed rather than throwing. A zero-row pass is a success, not an error —
// but a pass that LOST rows it had already parsed is a failure even though the HTTP call succeeded.
public class CbslMacroIngestionService : ICbslMacroIngestionService
{
    // Gating watermark key; the per-series rows are "CBSL_MACRO_<SeriesCode>".
    public const string SourceKey = "CBSL_MACRO";
    public const string SeriesSourcePrefix = "CBSL_MACRO_";

    private const string EnabledConfigKey = "MacroSources:CbslMacro:Enabled";
    private const string DisabledReason =
        "CBSL macro ingestion feature-flagged OFF (MacroSources:CbslMacro:Enabled=false) — documented no-op.";

    // The ML service's /admin/* routes require X-API-Key == ML_ADMIN_API_KEY, read from configuration and
    // never hardcoded — the same key the HARTI and news passes use.
    private const string AdminApiKeyConfigKey = "MlService:AdminApiKey";
    private const string AdminApiKeyHeaderName = "X-API-Key";

    // The ONLY summary status that means "everything this pass touched actually landed". The Python side
    // also emits "partial" (one or more artifacts lost their DB write); anything else is unrecognised.
    private const string OkStatus = "ok";

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IIngestionWatermarkRepository _watermarks;
    private readonly ILogger<CbslMacroIngestionService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CbslMacroIngestionService(
        HttpClient httpClient,
        IConfiguration configuration,
        IIngestionWatermarkRepository watermarks,
        ILogger<CbslMacroIngestionService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _watermarks = watermarks;
        _logger = logger;
    }

    public async Task<IngestionRunStats> IngestAsync(CancellationToken ct)
    {
        var enabled = _configuration.GetValue<bool>(EnabledConfigKey);

        // Default path: the feature flag is off. Make sure the gating watermark reflects the Disabled state
        // (creating it if absent) and return — a no-op, explicitly not a source failure.
        if (!enabled)
        {
            var gate = await _watermarks.GetOrCreateAsync(
                SourceKey, IngestionSourceStatus.Disabled, DisabledReason, ct);

            if (gate.Status != IngestionSourceStatus.Disabled)
            {
                gate.Disable(DisabledReason);
                await _watermarks.SaveChangesAsync(ct);
            }

            _logger.LogInformation(
                "CBSL macro ingestion: DISABLED (feature flag {Flag} is off) — skipping. {Reason}",
                EnabledConfigKey, DisabledReason);
            // A deliberate, documented no-op — Skipped, explicitly not a source failure.
            return Skipped();
        }

        // Enabled path. If ops explicitly Disabled the gating watermark out of band, honour that.
        var watermark = await _watermarks.GetOrCreateAsync(SourceKey, ct: ct);
        if (watermark.Status == IngestionSourceStatus.Disabled)
        {
            _logger.LogInformation(
                "CBSL macro ingestion: gating watermark is DISABLED ({Reason}) — skipping (not a failure).",
                watermark.LastMessage ?? "no reason recorded");
            return Skipped();
        }

        // Fail loud on a missing admin key: sending no header would surface as a confusing 401.
        var apiKey = _configuration[AdminApiKeyConfigKey];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Missing MlService:AdminApiKey. The ML service requires the X-API-Key header on " +
                "/admin/ingest-cbsl-macro (security fix F-02). Set it for local dev via " +
                "'dotnet user-secrets set \"MlService:AdminApiKey\" \"<value>\"', or in production " +
                "via the MlService__AdminApiKey environment variable. The value must match the ML " +
                "service's ML_ADMIN_API_KEY.");

        // The Python route drives itself from a DB watermark (the newest CCPI PublishedAt / MEI pack already
        // in MacroSeriesPoints, minus a one-month safety overlap), dropping artifacts it already has BEFORE
        // downloading them. There is no sinceDate knob to send: the resume point lives in the macro table
        // itself, not here. The route's `full` flag is the escape hatch that ignores that watermark and
        // re-scans the whole corpus — deliberately NOT sent, because a scheduled pass must stay incremental;
        // a full re-scan is a hand-run backfill (the CLI's --full). So only the orchestration flags go, at
        // their defaults.
        var payload = new IngestCbslMacroRequest();

        HttpResponseMessage resp;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "admin/ingest-cbsl-macro")
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            request.Headers.Add(AdminApiKeyHeaderName, apiKey);

            resp = await _httpClient.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // An admin stop, not an outage — let the audit wrapper record the distinct cancelled reason.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            const string reason = "Transport failure calling /admin/ingest-cbsl-macro.";
            _logger.LogWarning(ex,
                "CBSL macro ingestion: ML /admin/ingest-cbsl-macro call failed (service down or timed out).");
            watermark.RecordFailure(reason);
            await _watermarks.SaveChangesAsync(ct);
            // THE false-green path: the watermark already went red here, but the run row went green.
            return Failed(reason);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, ct);
                _logger.LogWarning(
                    "CBSL macro ingestion: ML /admin/ingest-cbsl-macro returned {StatusCode}. Body: {Body}",
                    (int)resp.StatusCode, body);
                var statusReason = $"ML returned {(int)resp.StatusCode}.";
                watermark.RecordFailure(statusReason);
                await _watermarks.SaveChangesAsync(ct);
                return Failed(statusReason);
            }

            IngestCbslMacroResponse? result;
            try
            {
                result = await resp.Content.ReadFromJsonAsync<IngestCbslMacroResponse>(JsonOptions, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "CBSL macro ingestion: could not parse /admin/ingest-cbsl-macro response.");
                const string parseReason = "Unparseable /admin/ingest-cbsl-macro response.";
                watermark.RecordFailure(parseReason);
                await _watermarks.SaveChangesAsync(ct);
                return Failed(parseReason);
            }

            var coverage = result?.PerSeriesCoverage ?? new Dictionary<string, int>();
            _logger.LogInformation(
                "CBSL macro ingestion: artifacts fetched {Fetched}/skipped {Skipped}/failed {Failed}, "
                + "{WatermarkSkipped} dropped by the watermark; rows inserted {Inserted}, "
                + "updated {Updated}, skipped-invalid {SkippedInvalid}; {SeriesCount} series covered.",
                result?.ArtifactsFetched ?? 0,
                result?.ArtifactsSkipped ?? 0,
                result?.ArtifactsFailed ?? 0,
                result?.ArtifactsWatermarkSkipped ?? 0,
                result?.RowsInserted ?? 0,
                result?.RowsUpdated ?? 0,
                result?.RowsSkippedInvalid ?? 0,
                coverage.Count);

            // HTTP 200 IS NOT THE WHOLE ANSWER. The Python route reports per-artifact DB-write failures in
            // the BODY and still answers 200 — its contract is "the pass ran, here is an honest report",
            // the same report-only shape /admin/ingest-harti uses. So the summary's own status is the gate:
            // status "partial" means one or more artifacts parsed fine and then LOST their rows to a DB
            // write, which is real missing data. Before this check that pass wrote a green run row, while
            // the same underlying failure on main threw, 502'd and correctly wrote red — the honest
            // reporting on the Python side would have quietly DOWNGRADED this alert.
            //
            // NULL / MISSING status counts as failure too, and that is safe rather than paranoid: every
            // version of ingest_cbsl_macro.run() that can produce a 200 sets "status" on every return path
            // ("ok" on main, "ok"|"partial" here), so an absent field is not an old image — it is a body
            // that is not this contract at all (a proxy, an error page, a JSON null). Guessing "probably
            // fine" there is exactly the false green this service was swept for.
            //
            // Ordinal, case-SENSITIVE: the Python side emits the literals "ok" and "partial" and nothing
            // else, so anything that is not exactly "ok" is unrecognised and treated as a failure. Being
            // strict here can only ever over-report, which is the safe direction; a case-insensitive match
            // would quietly accept a value nothing actually produces.
            if (!string.Equals(result?.Status, OkStatus, StringComparison.Ordinal))
            {
                var failedCount = result?.ArtifactsFailed ?? 0;
                var reason =
                    $"ML reported status '{result?.Status ?? "(missing)"}' — {failedCount} artifact(s) " +
                    $"failed their DB write; rows inserted {result?.RowsInserted ?? 0}, " +
                    $"updated {result?.RowsUpdated ?? 0}.";

                _logger.LogWarning(
                    "CBSL macro ingestion: ML answered 200 but reported status {Status} "
                    + "({FailedCount} artifact(s) lost their rows). Recording the pass as FAILED.",
                    result?.Status ?? "(missing)", failedCount);

                // No watermark is advanced — not the gating one and not a single per-series row. This pass
                // did not fully land, so nothing about it is a new high-water mark. (These rows are ops
                // bookkeeping only; the Python resume point lives in MacroSeriesPoints, so holding them
                // back cannot strand the next pass.)
                watermark.RecordFailure(reason);
                await _watermarks.SaveChangesAsync(ct);
                return Failed(reason);
            }

            var successUtc = DateTime.UtcNow;

            // Record a per-series watermark from perSeriesCoverage so one absent series never masks another.
            // A series with no coverage this pass is simply not in the dict and keeps its last-good value.
            foreach (var (seriesCode, rowCount) in coverage)
            {
                var seriesKey = SeriesSourcePrefix + seriesCode;
                var seriesWm = await _watermarks.GetOrCreateAsync(seriesKey, ct: ct);
                seriesWm.RecordSuccess(successUtc, message: $"rows={rowCount}");
            }

            // Advance the gating watermark on a confirmed success (bookkeeping only — there is no resume bound).
            watermark.RecordSuccess(
                successUtc,
                message: $"inserted={result?.RowsInserted ?? 0}, updated={result?.RowsUpdated ?? 0}, series={coverage.Count}");
            await _watermarks.SaveChangesAsync(ct);

            // A confirmed pass, including the common zero-new-rows case: the parser ran and reported.
            return new IngestionRunStats(
                RowsFetched: result?.ArtifactsFetched ?? 0,
                RowsInserted: result?.RowsInserted ?? 0,
                RowsSkipped: result?.RowsSkippedInvalid ?? 0);
        }
    }

    private static IngestionRunStats Skipped() =>
        new(Outcome: IngestionRunOutcome.Skipped);

    private static IngestionRunStats Failed(string reason) =>
        new(Outcome: IngestionRunOutcome.Failed, FailureReason: reason);

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch
        {
            return "<unreadable>";
        }
    }

    // Mirrors the Python IngestCbslMacroRequest (app.py): orchestration flags only, no sinceDate.
    private sealed class IngestCbslMacroRequest
    {
        [JsonPropertyName("noDownload")]
        public bool NoDownload { get; set; }

        [JsonPropertyName("dryRun")]
        public bool DryRun { get; set; }
    }

    // Mirrors the Python summary returned by ingest_cbsl_macro.run().
    private sealed class IngestCbslMacroResponse
    {
        // "ok" | "partial". READ, not decoration: it is the gate above, because the route answers 200 for
        // both. Nullable so a body missing the field deserializes and is then rejected on its merits,
        // rather than throwing and being reported as an unparseable response.
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("artifactsFetched")]
        public int ArtifactsFetched { get; set; }

        // Parsed cleanly, produced no series — a benign PDF content miss, NOT a failure.
        [JsonPropertyName("artifactsSkipped")]
        public int ArtifactsSkipped { get; set; }

        // Parsed cleanly and then LOST their rows to a DB write exception. This is the count that makes
        // status "partial"; it is reported in the failure reason so the run row names the damage.
        [JsonPropertyName("artifactsFailed")]
        public int ArtifactsFailed { get; set; }

        // Dropped by the Python DB watermark before download. High on a routine monthly pass and zero on a
        // backfill, so it is the number that shows the incremental fetch is working; logged, never gating.
        [JsonPropertyName("artifactsWatermarkSkipped")]
        public int ArtifactsWatermarkSkipped { get; set; }

        [JsonPropertyName("rowsInserted")]
        public int RowsInserted { get; set; }

        [JsonPropertyName("rowsUpdated")]
        public int RowsUpdated { get; set; }

        [JsonPropertyName("rowsSkippedInvalid")]
        public int RowsSkippedInvalid { get; set; }

        // { "<SeriesCode>": <rowCount> } — drives the per-series watermark rows.
        [JsonPropertyName("perSeriesCoverage")]
        public Dictionary<string, int>? PerSeriesCoverage { get; set; }
    }
}
