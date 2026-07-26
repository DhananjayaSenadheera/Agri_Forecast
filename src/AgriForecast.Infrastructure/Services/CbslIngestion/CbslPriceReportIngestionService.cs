using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgriForecast.Application.common;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.CbslIngestion;

// CBSL Daily Price Report ingestion (capture-only). The PDF parser is Python (agriforecast_ml/cbsl_report);
// this service orchestrates it over the same authenticated HTTP -> FastAPI seam as HARTI and News, via
// POST /admin/ingest-cbsl.
//
// The feature flag MarketPriceSources:Cbsl:Enabled is the source's pause switch. Flag off keeps the
// watermark Disabled, which is a documented no-op and never a source failure; flag on runs the pass even if
// a Disabled watermark was left behind by the flag-off era, and a successful pass flips it back to Ok.
//
// Resume mirrors HARTI: sinceDate = LastObservedDate - CbslLookbackDays (default 3), since a report for a
// holiday-adjacent date can appear around gaps and the idempotent upsert makes re-scanning free. A null
// watermark sends a null sinceDate and Python applies its own last-7-days default — a cold start must never
// trigger a silent historical backfill (deliberate backfills go through the CLI).
//
// Fail-safe but expressive: transport, HTTP or parse failures record a watermark failure WITHOUT advancing
// the resume point and return Outcome=Failed — never a throw to the Worker, never a green run row.
public class CbslPriceReportIngestionService : ICbslPriceReportIngestionService
{
    public const string SourceKey = "CBSL";

    private const string EnabledConfigKey = "MarketPriceSources:Cbsl:Enabled";
    private const string DisabledReason =
        "CBSL Daily Price Report ingestion feature-flagged OFF (MarketPriceSources:Cbsl:Enabled=false) — documented no-op.";

    // Same admin-key contract as HARTI/News (security fix F-02).
    private const string AdminApiKeyConfigKey = "MlService:AdminApiKey";
    private const string AdminApiKeyHeaderName = "X-API-Key";

    private const string LookbackDaysConfigKey = "MlService:CbslLookbackDays";
    private const int DefaultLookbackDays = 3;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IIngestionWatermarkRepository _watermarks;
    private readonly ILogger<CbslPriceReportIngestionService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CbslPriceReportIngestionService(
        HttpClient httpClient,
        IConfiguration configuration,
        IIngestionWatermarkRepository watermarks,
        ILogger<CbslPriceReportIngestionService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _watermarks = watermarks;
        _logger = logger;
    }

    public async Task<IngestionRunStats> IngestAsync(CancellationToken ct)
    {
        var enabled = _configuration.GetValue<bool>(EnabledConfigKey);

        if (!enabled)
        {
            // Flag off: keep the watermark Disabled (creating it if absent). A no-op, not a source failure.
            var disabledWm = await _watermarks.GetOrCreateAsync(
                SourceKey, IngestionSourceStatus.Disabled, DisabledReason, ct);

            if (disabledWm.Status != IngestionSourceStatus.Disabled)
            {
                disabledWm.Disable(DisabledReason);
                await _watermarks.SaveChangesAsync(ct);
            }

            _logger.LogInformation(
                "CBSL ingestion: DISABLED (feature flag {Flag} is off) — skipping. {Reason}",
                EnabledConfigKey, DisabledReason);
            return new IngestionRunStats(Outcome: IngestionRunOutcome.Skipped);
        }

        var watermark = await _watermarks.GetOrCreateAsync(SourceKey, ct: ct);

        // Fail loud on a missing admin key: a silently missing header would surface as a confusing 401.
        var apiKey = _configuration[AdminApiKeyConfigKey];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Missing MlService:AdminApiKey. The ML service requires the X-API-Key header on " +
                "/admin/ingest-cbsl (security fix F-02). Set it for local dev via " +
                "'dotnet user-secrets set \"MlService:AdminApiKey\" \"<value>\"', or in production " +
                "via the MlService__AdminApiKey environment variable. The value must match the ML " +
                "service's ML_ADMIN_API_KEY.");

        var lookbackDays = _configuration.GetValue<int?>(LookbackDaysConfigKey) ?? DefaultLookbackDays;
        if (lookbackDays < 0) lookbackDays = 0;   // never widen into the future; a negative would.
        DateOnly? sinceDate = watermark.LastObservedDate?.AddDays(-lookbackDays);
        var payload = new IngestCbslRequest
        {
            SinceDate = sinceDate?.ToString("yyyy-MM-dd")
        };

        HttpResponseMessage resp;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "admin/ingest-cbsl")
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            request.Headers.Add(AdminApiKeyHeaderName, apiKey);

            resp = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "CBSL ingestion: ML /admin/ingest-cbsl call failed (service down or timed out).");
            const string transportReason = "Transport failure calling /admin/ingest-cbsl.";
            watermark.RecordFailure(transportReason);
            await _watermarks.SaveChangesAsync(ct);
            return new IngestionRunStats(Outcome: IngestionRunOutcome.Failed, FailureReason: transportReason);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, ct);
                _logger.LogWarning(
                    "CBSL ingestion: ML /admin/ingest-cbsl returned {StatusCode}. Body: {Body}",
                    (int)resp.StatusCode, body);
                var statusReason = $"ML returned {(int)resp.StatusCode}.";
                watermark.RecordFailure(statusReason);
                await _watermarks.SaveChangesAsync(ct);
                return new IngestionRunStats(Outcome: IngestionRunOutcome.Failed, FailureReason: statusReason);
            }

            IngestCbslResponse? result;
            try
            {
                result = await resp.Content.ReadFromJsonAsync<IngestCbslResponse>(JsonOptions, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "CBSL ingestion: could not parse /admin/ingest-cbsl response.");
                const string parseReason = "Unparseable /admin/ingest-cbsl response.";
                watermark.RecordFailure(parseReason);
                await _watermarks.SaveChangesAsync(ct);
                return new IngestionRunStats(Outcome: IngestionRunOutcome.Failed, FailureReason: parseReason);
            }

            _logger.LogInformation(
                "CBSL ingestion: PriceObservations inserted {Inserted}, updated {Updated}, "
                + "no-market-skip {NoMarketSkip} (the two unseeded retail columns skip by design); "
                + "healed {Healed} crop(s), {Unresolved} label(s) left unresolved (CropId NULL, "
                + "never guessed); gaps info/warn/error {GapInfo}/{GapWarn}/{GapError}.",
                result?.PriceObservations?.Inserted ?? 0,
                result?.PriceObservations?.Updated ?? 0,
                result?.PriceObservations?.SkippedNoMarket ?? 0,
                result?.Heal?.Healed ?? 0,
                result?.Heal?.Unresolved ?? 0,
                result?.Gaps?.NInfo ?? 0,
                result?.Gaps?.NWarning ?? 0,
                result?.Gaps?.NError ?? 0);

            DateOnly? newest = TryParseDateOnly(result?.PriceObservations?.MaxObservedDate);
            watermark.RecordSuccess(
                DateTime.UtcNow,
                newest,
                $"inserted={result?.PriceObservations?.Inserted ?? 0}, updated={result?.PriceObservations?.Updated ?? 0}");
            await _watermarks.SaveChangesAsync(ct);

            return new IngestionRunStats(
                CoveredFromDate: sinceDate,
                CoveredToDate: newest,
                RowsInserted: result?.PriceObservations?.Inserted ?? 0,
                RowsSkipped: result?.PriceObservations?.SkippedNoMarket ?? 0);
        }
    }

    private static DateOnly? TryParseDateOnly(string? value)
        => DateOnly.TryParse(value, out var d) ? d : (DateOnly?)null;

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

    private sealed class IngestCbslRequest
    {
        // Resume lower bound (report date > sinceDate). Null means the Python side's last-7-days default.
        [JsonPropertyName("sinceDate")]
        public string? SinceDate { get; set; }
    }

    // The response shape is deliberately identical to /admin/ingest-harti's; the private mirror keeps the two
    // services decoupled in code.
    private sealed class IngestCbslResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("priceObservations")]
        public PriceObservationsSummary? PriceObservations { get; set; }

        [JsonPropertyName("heal")]
        public HealSummary? Heal { get; set; }

        [JsonPropertyName("gaps")]
        public GapSummary? Gaps { get; set; }
    }

    private sealed class PriceObservationsSummary
    {
        [JsonPropertyName("inserted")]
        public int Inserted { get; set; }

        [JsonPropertyName("updated")]
        public int Updated { get; set; }

        [JsonPropertyName("skippedNoMarket")]
        public int SkippedNoMarket { get; set; }

        [JsonPropertyName("maxObservedDate")]
        public string? MaxObservedDate { get; set; }
    }

    private sealed class HealSummary
    {
        [JsonPropertyName("healed")]
        public int Healed { get; set; }

        [JsonPropertyName("unresolved")]
        public int Unresolved { get; set; }
    }

    private sealed class GapSummary
    {
        [JsonPropertyName("nInfo")]
        public int NInfo { get; set; }

        [JsonPropertyName("nWarning")]
        public int NWarning { get; set; }

        [JsonPropertyName("nError")]
        public int NError { get; set; }
    }
}
