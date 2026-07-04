using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.CbslMacroIngestion;

// CBSL macro (CCPI / MEI vintage) ingestion (R1 P3, ClickUp 86cahefbh) — a thin, properly-wired
// SKELETON that is DISABLED by default, not faked. Hybrid of two existing house templates:
//   * gating/lifecycle from CbslPriceReportIngestionService (feature-flag OFF => documented no-op
//     success that is NEVER a source failure), and
//   * the HTTP->Python seam from HartiBulletinIngestionService (authenticated X-API-Key POST to an
//     /admin/* route on the same ML service, fail-safe transport handling that never throws to the
//     Worker).
//
// SINGLE SOURCE OF TRUTH: the CBSL PDF fetch/parse/upsert is Python and STAYS Python
// (ingest_cbsl_macro.py). This .NET service does NOT parse PDFs; it triggers the Python pass over
// the shared HTTP seam via POST /admin/ingest-cbsl-macro, then records per-series watermarks from
// the returned perSeriesCoverage.
//
// FEATURE FLAG (why Disabled matters): the flag MacroSources:CbslMacro:Enabled defaults FALSE. A
// Disabled source is a NO-OP and is NEVER counted as a source failure — a source we chose not to
// run must not pollute the fail-isolation signal or trip alerting. The gating watermark row
// (Source = "CBSL_MACRO") is created/kept Disabled with the reason recorded so ops can see at a
// glance that macro ingestion is intentionally off (distinct from "failing").
//
// PER-SERIES WATERMARKS (adjudicated from the real Python response): the /admin/ingest-cbsl-macro
// response carries perSeriesCoverage = { "<SeriesCode>": <rowCount>, ... } (e.g.
// { "CCPI_BASE2021": 3, "FOOD_INFLATION_YOY": 3 }). Because per-series detail IS present cleanly,
// we record ONE IngestionWatermark PER SERIES keyed "CBSL_MACRO_<SeriesCode>" (matching the
// recorded contract "per-series IngestionWatermark rows, e.g. CBSL_CCPI"), so one late/absent
// series never masks or fails another. Note the Python pass is a FULL re-scrape each time (small
// bounded corpus, no sinceDate knob), so these watermarks are success/coverage bookkeeping, not a
// resume lower bound — the request carries no sinceDate.
//
// ENABLING LATER: set MacroSources:CbslMacro:Enabled = true (the Python route + 159 rows already
// exist). The enabled path then POSTs to /admin/ingest-cbsl-macro; a transport/HTTP failure is
// recorded on the gating watermark WITHOUT advancing anything and returns (never throws) — the
// Worker's per-source try/catch is the outer belt, this is the inner suspenders.
//
// UPDATE PATH: the corpus is monthly; run-monthly.sh (~15th) drives the pass once enabled.
// Zero-new-rows ("no new bulletin since last pass") is a SUCCESS with zero rows, never an error.
public class CbslMacroIngestionService : ICbslMacroIngestionService
{
    // Gating watermark key (owns the Disabled/failed lifecycle for the whole pass). Per-series
    // rows are "CBSL_MACRO_<SeriesCode>".
    public const string SourceKey = "CBSL_MACRO";
    public const string SeriesSourcePrefix = "CBSL_MACRO_";

    private const string EnabledConfigKey = "MacroSources:CbslMacro:Enabled";
    private const string DisabledReason =
        "CBSL macro ingestion feature-flagged OFF (MacroSources:CbslMacro:Enabled=false) — documented no-op.";

    // The ML service's /admin/* routes require X-API-Key == ML_ADMIN_API_KEY (security fix F-02),
    // read from configuration (never hardcoded) — same key the HARTI/news passes use.
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

        // Default (and current) path: feature-flagged OFF. Ensure the gating watermark reflects the
        // Disabled state (create it Disabled if absent, or move it to Disabled if it was left in some
        // other state) and return — a no-op that is explicitly NOT a source failure.
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

        // Enabled path. If ops explicitly Disabled the gating watermark out-of-band, honour that
        // (a no-op, never a failure) even though the flag is on.
        var watermark = await _watermarks.GetOrCreateAsync(SourceKey, ct: ct);
        if (watermark.Status == IngestionSourceStatus.Disabled)
        {
            _logger.LogInformation(
                "CBSL macro ingestion: gating watermark is DISABLED ({Reason}) — skipping (not a failure).",
                watermark.LastMessage ?? "no reason recorded");
            return;
        }

        // Fail loud on a missing admin key (mirrors HartiBulletinIngestionService / F-01 guard):
        // sending no header would surface as a confusing 401 from the ML service.
        var apiKey = _configuration[AdminApiKeyConfigKey];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Missing MlService:AdminApiKey. The ML service requires the X-API-Key header on " +
                "/admin/ingest-cbsl-macro (security fix F-02). Set it for local dev via " +
                "'dotnet user-secrets set \"MlService:AdminApiKey\" \"<value>\"', or in production " +
                "via the MlService__AdminApiKey environment variable. The value must match the ML " +
                "service's ML_ADMIN_API_KEY.");

        // The macro corpus is a small bounded set (tens of PDFs); the Python route full-re-scrapes
        // every pass and exposes no sinceDate knob, so we send only the orchestration flags at
        // their defaults (real download, real DB write).
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

            // Record a PER-SERIES watermark from perSeriesCoverage so one absent series never masks
            // another. A series with zero coverage this pass simply is not present in the dict; its
            // existing watermark (if any) keeps its last-good value — a no-new-rows pass is a success.
            foreach (var (seriesCode, rowCount) in coverage)
            {
                var seriesKey = SeriesSourcePrefix + seriesCode;
                var seriesWm = await _watermarks.GetOrCreateAsync(seriesKey, ct: ct);
                seriesWm.RecordSuccess(successUtc, message: $"rows={rowCount}");
            }

            // Advance the gating watermark on a confirmed success (bookkeeping only; the request
            // carries no resume lower bound).
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
