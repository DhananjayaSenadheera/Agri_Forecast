using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
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
// keyed "CBSL_MACRO_<SeriesCode>", and one late or absent series never masks another. The Python pass is a
// full re-scrape each time (a small bounded corpus, no sinceDate knob), so these watermarks are coverage
// bookkeeping rather than a resume lower bound.
//
// To enable later, set MacroSources:CbslMacro:Enabled = true. A transport or HTTP failure is recorded on the
// gating watermark without advancing anything and returns rather than throwing. A zero-row pass is a
// success, not an error.
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

    public async Task IngestAsync(CancellationToken ct)
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
            return;
        }

        // Enabled path. If ops explicitly Disabled the gating watermark out of band, honour that.
        var watermark = await _watermarks.GetOrCreateAsync(SourceKey, ct: ct);
        if (watermark.Status == IngestionSourceStatus.Disabled)
        {
            _logger.LogInformation(
                "CBSL macro ingestion: gating watermark is DISABLED ({Reason}) — skipping (not a failure).",
                watermark.LastMessage ?? "no reason recorded");
            return;
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

        // The Python route full-re-scrapes every pass and exposes no sinceDate knob, so only the orchestration
        // flags are sent, at their defaults.
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "CBSL macro ingestion: ML /admin/ingest-cbsl-macro call failed (service down or timed out).");
            watermark.RecordFailure("Transport failure calling /admin/ingest-cbsl-macro.");
            await _watermarks.SaveChangesAsync(ct);
            return;
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, ct);
                _logger.LogWarning(
                    "CBSL macro ingestion: ML /admin/ingest-cbsl-macro returned {StatusCode}. Body: {Body}",
                    (int)resp.StatusCode, body);
                watermark.RecordFailure($"ML returned {(int)resp.StatusCode}.");
                await _watermarks.SaveChangesAsync(ct);
                return;
            }

            IngestCbslMacroResponse? result;
            try
            {
                result = await resp.Content.ReadFromJsonAsync<IngestCbslMacroResponse>(JsonOptions, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "CBSL macro ingestion: could not parse /admin/ingest-cbsl-macro response.");
                watermark.RecordFailure("Unparseable /admin/ingest-cbsl-macro response.");
                await _watermarks.SaveChangesAsync(ct);
                return;
            }

            var coverage = result?.PerSeriesCoverage ?? new Dictionary<string, int>();
            _logger.LogInformation(
                "CBSL macro ingestion: artifacts fetched {Fetched}/skipped {Skipped}; rows inserted {Inserted}, "
                + "updated {Updated}, skipped-invalid {SkippedInvalid}; {SeriesCount} series covered.",
                result?.ArtifactsFetched ?? 0,
                result?.ArtifactsSkipped ?? 0,
                result?.RowsInserted ?? 0,
                result?.RowsUpdated ?? 0,
                result?.RowsSkippedInvalid ?? 0,
                coverage.Count);

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
        }
    }

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
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("artifactsFetched")]
        public int ArtifactsFetched { get; set; }

        [JsonPropertyName("artifactsSkipped")]
        public int ArtifactsSkipped { get; set; }

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
