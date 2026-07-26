using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.NewsIngestion;

// Triggers the Python news pipeline (ingest_news -> score_news) once per daily pass, via the FastAPI serving
// app's POST /admin/ingest-news.
// Transport is a typed HttpClient over the same Python ML service the rest of the app already talks to, so
// the Worker host needs no Python venv on disk.
// Fails safe: never throws to the Worker on a transport or HTTP error — the pass is logged and continues. A
// non-2xx is logged with body and status, not swallowed silently.
public class NewsIngestionService : INewsIngestionService
{
    // /admin/ingest-news requires X-API-Key equal to the ML service's ML_ADMIN_API_KEY. The key is read from
    // configuration and never hardcoded: user-secrets in dev, MlService__AdminApiKey in prod.
    private const string AdminApiKeyConfigKey = "MlService:AdminApiKey";
    private const string AdminApiKeyHeaderName = "X-API-Key";

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NewsIngestionService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public NewsIngestionService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<NewsIngestionService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task IngestAsync(CancellationToken ct)
    {
        // Fail loud: if the admin key is absent, throw a clear configuration error rather than sending no
        // header, which would surface as a confusing 401 from the ML service.
        var apiKey = _configuration[AdminApiKeyConfigKey];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Missing MlService:AdminApiKey. The ML service requires the X-API-Key " +
                "header on /admin/ingest-news (security fix F-02). Set it for local dev via " +
                "'dotnet user-secrets set \"MlService:AdminApiKey\" \"<value>\"', or in " +
                "production via the MlService__AdminApiKey environment variable. The value " +
                "must match the ML service's ML_ADMIN_API_KEY.");

        // Defaults run the full live pipeline: fetch RSS, write and QA, then score and write daily sentiment.
        var payload = new IngestNewsRequest();

        HttpResponseMessage resp;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "admin/ingest-news")
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            request.Headers.Add(AdminApiKeyHeaderName, apiKey);

            resp = await _httpClient.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "News ingestion: ML /admin/ingest-news call failed (service down or timed out).");
            return;
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBodyAsync(resp, ct);
                _logger.LogWarning(
                    "News ingestion: ML /admin/ingest-news returned {StatusCode}. Body: {Body}",
                    (int)resp.StatusCode, body);
                return;
            }

            IngestNewsResponse? result = null;
            try
            {
                result = await resp.Content.ReadFromJsonAsync<IngestNewsResponse>(JsonOptions, ct);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "News ingestion: could not parse /admin/ingest-news response.");
                return;
            }

            _logger.LogInformation(
                "News ingestion: inserted {Inserted} article(s) (dupSkipped {DupSkipped}), "
                + "scored {ArticlesScored} -> {RowsWritten} daily sentiment row(s).",
                result?.Ingest?.Inserted ?? 0,
                result?.Ingest?.DupSkipped ?? 0,
                result?.Score?.ArticlesScored ?? 0,
                result?.Score?.RowsWritten ?? 0);
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

    private sealed class IngestNewsRequest
    {
        [JsonPropertyName("dryRun")]
        public bool DryRun { get; set; }

        [JsonPropertyName("skipQa")]
        public bool SkipQa { get; set; }

        [JsonPropertyName("writebackScores")]
        public bool WritebackScores { get; set; }
    }

    private sealed class IngestNewsResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("ingest")]
        public IngestSummary? Ingest { get; set; }

        [JsonPropertyName("score")]
        public ScoreSummary? Score { get; set; }
    }

    private sealed class IngestSummary
    {
        [JsonPropertyName("inserted")]
        public int Inserted { get; set; }

        [JsonPropertyName("dupSkipped")]
        public int DupSkipped { get; set; }
    }

    private sealed class ScoreSummary
    {
        [JsonPropertyName("articlesScored")]
        public int ArticlesScored { get; set; }

        [JsonPropertyName("rowsWritten")]
        public int RowsWritten { get; set; }
    }
}
