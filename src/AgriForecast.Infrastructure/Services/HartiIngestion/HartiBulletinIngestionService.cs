using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgriForecast.Application.common;
using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.HartiIngestion;


public class HartiBulletinIngestionService : IHartiBulletinIngestionService
{
    public const string SourceKey = "HARTI";

    // The ML service's /admin/* routes require X-API-Key == ML_ADMIN_API_KEY, read from configuration and
    // never hardcoded — the same key the news pass uses.
    private const string AdminApiKeyConfigKey = "MlService:AdminApiKey";
    private const string AdminApiKeyHeaderName = "X-API-Key";

    // Late-arrival look-back (days) subtracted from the watermark to compute the resume lower bound, so a
    // bulletin published late for an already-passed date is not skipped forever. Default 7; the idempotent
    // upsert makes re-scanning the window free.
    private const string LookbackDaysConfigKey = "MlService:HartiLookbackDays";
    private const int DefaultLookbackDays = 7;

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IIngestionWatermarkRepository _watermarks;
    private readonly ILogger<HartiBulletinIngestionService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public HartiBulletinIngestionService(
        HttpClient httpClient,
        IConfiguration configuration,
        IIngestionWatermarkRepository watermarks,
        ILogger<HartiBulletinIngestionService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _watermarks = watermarks;
        _logger = logger;
    }

    public async Task<IngestionRunStats> IngestAsync(CancellationToken ct)
    {
        var watermark = await _watermarks.GetOrCreateAsync(SourceKey, ct: ct);

        if (watermark.Status == Domain.Enums.IngestionSourceStatus.Disabled)
        {
            _logger.LogInformation(
                "HARTI ingestion: source is DISABLED ({Reason}) — skipping (not a failure).",
                watermark.LastMessage ?? "no reason recorded");
            // Expressive outcome: a disabled source is a SKIP, not a green success (S1).
            return new IngestionRunStats(Outcome: IngestionRunOutcome.Skipped);
        }

        // Fail loud on a missing admin key: sending no header would surface as a confusing 401.
        var apiKey = _configuration[AdminApiKeyConfigKey];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Missing MlService:AdminApiKey. The ML service requires the X-API-Key header on " +
                "/admin/ingest-harti (security fix F-02). Set it for local dev via " +
                "'dotnet user-secrets set \"MlService:AdminApiKey\" \"<value>\"', or in production " +
                "via the MlService__AdminApiKey environment variable. The value must match the ML " +
                "service's ML_ADMIN_API_KEY.");

        
        var lookbackDays = _configuration.GetValue<int?>(LookbackDaysConfigKey) ?? DefaultLookbackDays;
        if (lookbackDays < 0) lookbackDays = 0;   // never widen into the future; a negative would.
        DateOnly? sinceDate = watermark.LastObservedDate?.AddDays(-lookbackDays);
        var payload = new IngestHartiRequest
        {
            SinceDate = sinceDate?.ToString("yyyy-MM-dd")
        };

        HttpResponseMessage resp;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "admin/ingest-harti")
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            request.Headers.Add(AdminApiKeyHeaderName, apiKey);

            resp = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex,
                "HARTI ingestion: ML /admin/ingest-harti call failed (service down or timed out). "
                + "If this is a first-run cold backfill (empty watermark), the full ~3000-PDF corpus "
                + "may exceed the HTTP timeout — seed via the ingest_harti.py CLI once (no HTTP timeout), "
                + "then the incremental Worker passes take over. See docs.");
            const string transportReason = "Transport failure calling /admin/ingest-harti (first-run backfill may exceed the HTTP timeout — seed via ingest_harti.py CLI, see docs).";
            watermark.RecordFailure(transportReason);
            await _watermarks.SaveChangesAsync(ct);
            // Fail-safe but expressive: never throws to the Worker, yet the run row is marked Failed with the
            // same reason the watermark got, not a green Succeeded.
            return new IngestionRunStats(Outcome: IngestionRunOutcome.Failed, FailureReason: transportReason);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, ct);
                _logger.LogWarning(
                    "HARTI ingestion: ML /admin/ingest-harti returned {StatusCode}. Body: {Body}",
                    (int)resp.StatusCode, body);
                var statusReason = $"ML returned {(int)resp.StatusCode}.";
                watermark.RecordFailure(statusReason);
                await _watermarks.SaveChangesAsync(ct);
                return new IngestionRunStats(Outcome: IngestionRunOutcome.Failed, FailureReason: statusReason);
            }

            IngestHartiResponse? result;
            try
            {
                result = await resp.Content.ReadFromJsonAsync<IngestHartiResponse>(JsonOptions, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "HARTI ingestion: could not parse /admin/ingest-harti response.");
                const string parseReason = "Unparseable /admin/ingest-harti response.";
                watermark.RecordFailure(parseReason);
                await _watermarks.SaveChangesAsync(ct);
                return new IngestionRunStats(Outcome: IngestionRunOutcome.Failed, FailureReason: parseReason);
            }

            _logger.LogInformation(
                "HARTI ingestion: PriceObservations inserted {Inserted}, updated {Updated}, "
                + "no-market-skip {NoMarketSkip}; healed {Healed} crop(s), {Unresolved} label(s) "
                + "left unresolved (CropId NULL, never guessed); {OutliersFlagged} outlier(s) flagged; "
                + "gaps info/warn/error {GapInfo}/{GapWarn}/{GapError}.",
                result?.PriceObservations?.Inserted ?? 0,
                result?.PriceObservations?.Updated ?? 0,
                result?.PriceObservations?.SkippedNoMarket ?? 0,
                result?.Heal?.Healed ?? 0,
                result?.Heal?.Unresolved ?? 0,
                result?.Outliers?.NFlagged ?? 0,
                result?.Gaps?.NInfo ?? 0,
                result?.Gaps?.NWarning ?? 0,
                result?.Gaps?.NError ?? 0);

            // Advance the resume watermark ONLY on a confirmed success. LastObservedDate is the newest date
            // the Python side landed this pass; null keeps the previous high-water mark.
            DateOnly? newest = TryParseDateOnly(result?.PriceObservations?.MaxObservedDate);
            watermark.RecordSuccess(
                DateTime.UtcNow,
                newest,
                $"inserted={result?.PriceObservations?.Inserted ?? 0}, updated={result?.PriceObservations?.Updated ?? 0}");
            await _watermarks.SaveChangesAsync(ct);

            // Map the counts the Python side returned onto the run row. The coverage window runs from the
            // requested resume lower bound to the newest landed ObservedDate; "updated" has no run-stats column.
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

    private sealed class IngestHartiRequest
    {
        // Resume lower bound (ObservedDate > sinceDate). Null => full backfill.
        [JsonPropertyName("sinceDate")]
        public string? SinceDate { get; set; }
    }

    private sealed class IngestHartiResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("priceObservations")]
        public PriceObservationsSummary? PriceObservations { get; set; }

        [JsonPropertyName("heal")]
        public HealSummary? Heal { get; set; }

        [JsonPropertyName("outliers")]
        public OutlierSummary? Outliers { get; set; }

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

        // Newest ObservedDate landed this pass (yyyy-MM-dd) — the watermark high-water mark.
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

    private sealed class OutlierSummary
    {
        [JsonPropertyName("nFlagged")]
        public int NFlagged { get; set; }
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
