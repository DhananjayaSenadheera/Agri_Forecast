using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgriForecast.Application.common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.NewsIngestion;

// Triggers the Python news pipeline (ingest_news -> score_news) once per daily pass, via the FastAPI serving
// app's POST /admin/ingest-news.
// Transport is a typed HttpClient over the same Python ML service the rest of the app already talks to, so
// the Worker host needs no Python venv on disk.
//
// Fails safe: never throws to the pass on a transport or HTTP error, so one dead source cannot abort the
// others. But fail-safe is NOT the same as fail-silent — every one of those swallowed errors now returns
// IngestionRunStats with Outcome=Failed and a short reason, so the run row goes red.
// This is the "NEWS false-green" fix: previously each error path just logged a warning and returned void,
// and the audit wrapper — seeing a body that completed normally — wrote a green Succeeded row. The admin
// ingestion card therefore reported healthy news ingestion on days the ML service was down.
// A pass that genuinely ran and found nothing new (200, zero inserted) stays Succeeded: "no new articles"
// is a real success and must not be dressed up as a failure.
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

    public async Task<IngestionRunStats> IngestAsync(CancellationToken ct)
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // An admin stop, NOT a timeout. HttpClient signals both as TaskCanceledException, so without
            // this filter a deliberate stop would be recorded as "service down or timed out" and send an
            // operator hunting for an ML outage that never happened. Rethrow so the audit wrapper applies
            // its distinct cancelled reason.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Covers the service being down, DNS/connection refusal, and the client timeout. All of these
            // mean the pipeline did not run — the very case that used to go green.
            _logger.LogWarning(ex, "News ingestion: ML /admin/ingest-news call failed (service down or timed out).");
            return Failed("The ML service call to /admin/ingest-news failed (service down or timed out).");
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                // The Python route raises 502/503 when the ingest or scoring step fails, so a non-2xx is a
                // real pipeline failure, not a transport hiccup. The status code goes on the run row; the
                // body is logged only, since it can be long and is not sanitized for the admin UI.
                var body = await SafeReadBodyAsync(resp, ct);
                _logger.LogWarning(
                    "News ingestion: ML /admin/ingest-news returned {StatusCode}. Body: {Body}",
                    (int)resp.StatusCode, body);
                return Failed($"The ML service returned HTTP {(int)resp.StatusCode} from /admin/ingest-news.");
            }

            IngestNewsResponse? result;
            try
            {
                result = await resp.Content.ReadFromJsonAsync<IngestNewsResponse>(JsonOptions, ct);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                _logger.LogWarning(ex, "News ingestion: could not parse /admin/ingest-news response.");
                return Failed("The ML service response to /admin/ingest-news could not be parsed.");
            }

            // A 2xx we cannot read as a result is not a confirmed success. Say so rather than assume.
            if (result is null)
                return Failed("The ML service returned an empty response body from /admin/ingest-news.");

            // The route answers {"status":"ok", ...} on success. Anything else on a 2xx means the pipeline
            // reported a problem the HTTP status did not.
            if (!string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "News ingestion: /admin/ingest-news reported status '{Status}'.", result.Status);
                return Failed($"The ML news pipeline reported status '{result.Status}'.");
            }

            var inserted = result.Ingest?.Inserted ?? 0;
            var dupSkipped = result.Ingest?.DupSkipped ?? 0;

            _logger.LogInformation(
                "News ingestion: inserted {Inserted} article(s) (dupSkipped {DupSkipped}), "
                + "scored {ArticlesScored} -> {RowsWritten} daily sentiment row(s).",
                inserted,
                dupSkipped,
                result.Score?.ArticlesScored ?? 0,
                result.Score?.RowsWritten ?? 0);

            // Success, including the quiet-news-day case of zero inserted: the pipeline ran and reported.
            return new IngestionRunStats(
                RowsInserted: inserted,
                RowsSkipped: dupSkipped);
        }
    }

    // A swallowed error, reported honestly. The reason is code-authored and short — it lands on the run
    // row's error summary, which the admin ingestion page shows.
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
