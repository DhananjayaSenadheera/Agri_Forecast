using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgriForecast.Domain.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.HartiIngestion;

// Orchestrates the multi-market HARTI daily-bulletin ingestion (R1.1 P1 Step 6).
//
// SINGLE SOURCE OF TRUTH: the HARTI PDF parser is Python and STAYS Python. This .NET service
// does NOT parse PDFs. It triggers the Python pipeline over the same HTTP->FastAPI seam the
// rest of the app already uses (mirroring NewsIngestionService), via the authenticated
// POST /admin/ingest-harti endpoint, then awaits + reports counts and the post-pass
// data-quality summary the Python side returns.
//
// Watermark (resumable): before triggering it reads the "HARTI" IngestionWatermark; if the
// source is Disabled it is a no-op (and never a failure). After a successful trigger it
// advances LastSuccessUtc + LastObservedDate. A transport/HTTP failure records a Failure on
// the watermark WITHOUT moving the resume point, then returns (never throws to the Worker) —
// the Worker's own per-source try/catch is the outer belt; this is the inner suspenders.
//
// Late-arrival look-back: the resume lower bound is NOT the raw watermark. HARTI can publish a
// bulletin late for a date at/behind the watermark; a strict "ObservedDate > LastObservedDate"
// resume would skip that late bulletin forever. So sinceDate = LastObservedDate - HartiLookbackDays
// (config MlService:HartiLookbackDays, default 7), re-scanning roughly the last week every pass.
// The upsert is idempotent, so re-scanning already-landed dates is free (existing rows just
// UPDATE; a genuinely-late bulletin in the window finally lands). A NULL watermark stays NULL =>
// full backfill (see the first-run note below).
//
// FIRST-RUN BOOTSTRAP: a cold, empty-watermark full backfill parses the entire ~3000-PDF corpus
// and can exceed HartiIngestTimeoutSeconds (default 1800s), so it may never bootstrap over HTTP.
// The sanctioned first-time seed is to run the CLI `python ingest_harti.py` once (no HTTP
// timeout); after that these incremental Worker passes take over. The transport-timeout log
// message below says exactly this so an operator hitting the timeout knows what to do.
//
// SELF-HEALING is the canonical layer's job, run in-process on the Python side as part of the
// same admin call (heal_price_observation_crops): unresolved commodity labels stay CropId NULL
// and are logged, NEVER auto-provisioned into duplicate crops. Unknown markets are WARN-skipped,
// never invented. This service surfaces those counts; it does not itself mint crops or markets.
public class HartiBulletinIngestionService : IHartiBulletinIngestionService
{
    public const string SourceKey = "HARTI";

    // The ML service's /admin/* routes require X-API-Key == ML_ADMIN_API_KEY (security fix F-02),
    // read from configuration (never hardcoded) — same key the news pass uses.
    private const string AdminApiKeyConfigKey = "MlService:AdminApiKey";
    private const string AdminApiKeyHeaderName = "X-API-Key";

    // Late-arrival look-back window (days) subtracted from the watermark when computing the
    // resume lower bound, so a bulletin published late for a date at/behind the watermark is not
    // skipped forever. Default 7; the idempotent upsert makes re-scanning the window free.
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

    public async Task IngestAsync(CancellationToken ct)
    {
        var watermark = await _watermarks.GetOrCreateAsync(SourceKey, ct: ct);

        if (watermark.Status == Domain.Enums.IngestionSourceStatus.Disabled)
        {
            _logger.LogInformation(
                "HARTI ingestion: source is DISABLED ({Reason}) — skipping (not a failure).",
                watermark.LastMessage ?? "no reason recorded");
            return;
        }

        // Fail loud on missing admin key (mirrors NewsIngestionService / SqlDbService F-01 guard):
        // sending no header would surface as a confusing 401 from the ML service.
        var apiKey = _configuration[AdminApiKeyConfigKey];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Missing MlService:AdminApiKey. The ML service requires the X-API-Key header on " +
                "/admin/ingest-harti (security fix F-02). Set it for local dev via " +
                "'dotnet user-secrets set \"MlService:AdminApiKey\" \"<value>\"', or in production " +
                "via the MlService__AdminApiKey environment variable. The value must match the ML " +
                "service's ML_ADMIN_API_KEY.");

        // Resume hint with a late-arrival look-back: fetch bulletins after (LastObservedDate -
        // HartiLookbackDays), so the daily pass does not re-scrape the whole ~3000-PDF corpus yet
        // still re-scans roughly the last week to catch a bulletin published late for an
        // already-passed date. Idempotent upsert => re-scanning the window is free. Null watermark
        // stays null => full backfill (the Python side treats a null sinceDate as "no lower bound").
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
            watermark.RecordFailure("Transport failure calling /admin/ingest-harti (first-run backfill may exceed the HTTP timeout — seed via ingest_harti.py CLI, see docs).");
            await _watermarks.SaveChangesAsync(ct);
            return;
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, ct);
                _logger.LogWarning(
                    "HARTI ingestion: ML /admin/ingest-harti returned {StatusCode}. Body: {Body}",
                    (int)resp.StatusCode, body);
                watermark.RecordFailure($"ML returned {(int)resp.StatusCode}.");
                await _watermarks.SaveChangesAsync(ct);
                return;
            }

            IngestHartiResponse? result;
            try
            {
                result = await resp.Content.ReadFromJsonAsync<IngestHartiResponse>(JsonOptions, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "HARTI ingestion: could not parse /admin/ingest-harti response.");
                watermark.RecordFailure("Unparseable /admin/ingest-harti response.");
                await _watermarks.SaveChangesAsync(ct);
                return;
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

            // Advance the resume watermark ONLY on a confirmed success. LastObservedDate is the
            // newest ObservedDate the Python side landed this pass (null if it landed nothing new,
            // in which case RecordSuccess keeps the previous high-water mark).
            DateOnly? newest = TryParseDateOnly(result?.PriceObservations?.MaxObservedDate);
            watermark.RecordSuccess(
                DateTime.UtcNow,
                newest,
                $"inserted={result?.PriceObservations?.Inserted ?? 0}, updated={result?.PriceObservations?.Updated ?? 0}");
            await _watermarks.SaveChangesAsync(ct);
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
