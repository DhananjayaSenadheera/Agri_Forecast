using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.NewsIngestion;

// Triggers the Python news pipeline (ingest_news -> score_news) once per daily
// pass by calling the FastAPI serving app's POST /admin/ingest-news endpoint.
//
// Transport: typed HttpClient over the same Python ML service the rest of the
// app already talks to (MlService:BaseUrl), mirroring HarvestPredictionClient.
// The repo's only .NET->Python integration is HTTP->FastAPI; reusing it avoids
// coupling the Worker host to a Python venv on disk (no subprocess precedent).
//
// Fails safe: never throws to the Worker on a transport/HTTP error. The pass
// is logged and continues, exactly like the other ingestion steps. A non-2xx
// (the Python side surfaces a structured 502/503 on pipeline failure) is logged
// with body + status, not swallowed silently.
public class NewsIngestionService : INewsIngestionService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NewsIngestionService> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public NewsIngestionService(HttpClient httpClient, ILogger<NewsIngestionService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task IngestAsync(CancellationToken ct)
    {
        // Defaults run the full live pipeline: fetch RSS + write + QA, then
        // score + write the daily sentiment table.
        var payload = new IngestNewsRequest();

        HttpResponseMessage resp;
        try
        {
            resp = await _httpClient.PostAsJsonAsync("admin/ingest-news", payload, JsonOptions, ct);
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
