using System.Net.Http.Json;
using System.Text.Json;
using AgriForecast.Infrastructure.ExternalSources.DTOs;

namespace AgriForecast.Infrastructure.ExternalSources.Interfaces;

public sealed class DambullaApiClient : IDambullaApiClient
{
    private readonly HttpClient _httpClient;

    public DambullaApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }
    public async Task<List<DambullaChartItemDto>?> GetProductPriceChartAsync(int productId, CancellationToken ct)
    {
        var path = $"api/prices/product/{productId}/chart";

        using var resp = await _httpClient.GetAsync(path, ct);
        if (!resp.IsSuccessStatusCode)
            return null;

        var raw = await resp.Content.ReadAsStringAsync(ct);

        try
        {
            return JsonSerializer.Deserialize<List<DambullaChartItemDto>>(
                raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            var preview = raw.Length > 300 ? raw[..300] : raw;
            throw new InvalidOperationException($"Failed to parse JSON for productId={productId}. Preview: {preview}");
        }
    }
}