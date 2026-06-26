using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgriForecast.Application.Services;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.ExternalSources.Interfaces;

// Typed HttpClient over the Python FastAPI ML service POST /predict.
// Fails safe: returns null on any non-success / exception so the handler can
// surface a structured error instead of leaking a 500.
public sealed class HarvestPredictionClient : IHarvestPredictionClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<HarvestPredictionClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public HarvestPredictionClient(HttpClient httpClient, ILogger<HarvestPredictionClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<HarvestPredictionDto?> PredictAsync(Guid cropId, DateOnly plantDate, CancellationToken ct = default)
    {
        // GUIDs on the wire MUST be lowercase - uppercase ids silently miss the
        // model's per-crop fallback. Guid.ToString() is already lowercase; keep it.
        var payload = new PredictRequest
        {
            CropId = cropId.ToString(),
            PlantDate = plantDate.ToString("yyyy-MM-dd")
        };

        try
        {
            using var resp = await _httpClient.PostAsJsonAsync("predict", payload, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ML /predict returned {StatusCode} for cropId={CropId}.",
                    (int)resp.StatusCode, payload.CropId);
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<HarvestPredictionDto>(JsonOptions, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "ML /predict call failed for cropId={CropId}.", payload.CropId);
            return null;
        }
    }

    public async Task<CropTimelineDto?> GetTimelineAsync(Guid cropId, DateOnly? asOf, int months, CancellationToken ct = default)
    {
        // GUIDs on the wire MUST be lowercase - uppercase ids silently miss the
        // model's per-crop fallback. Guid.ToString() is already lowercase; keep it.
        // asOf null => Python defaults to today.
        var payload = new TimelineRequest
        {
            CropId = cropId.ToString(),
            AsOf = asOf?.ToString("yyyy-MM-dd"),
            Months = months
        };

        try
        {
            using var resp = await _httpClient.PostAsJsonAsync("timeline", payload, JsonOptions, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ML /timeline returned {StatusCode} for cropId={CropId}.",
                    (int)resp.StatusCode, payload.CropId);
                return null;
            }

            return await resp.Content.ReadFromJsonAsync<CropTimelineDto>(JsonOptions, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogWarning(ex, "ML /timeline call failed for cropId={CropId}.", payload.CropId);
            return null;
        }
    }

    private sealed class PredictRequest
    {
        [JsonPropertyName("cropId")]
        public string CropId { get; set; } = string.Empty;

        [JsonPropertyName("plantDate")]
        public string PlantDate { get; set; } = string.Empty;
    }

    private sealed class TimelineRequest
    {
        [JsonPropertyName("cropId")]
        public string CropId { get; set; } = string.Empty;

        [JsonPropertyName("asOf")]
        public string? AsOf { get; set; }

        [JsonPropertyName("months")]
        public int Months { get; set; }
    }
}
