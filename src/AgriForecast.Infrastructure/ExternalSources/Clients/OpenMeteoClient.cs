using System.Globalization;
using System.Text.Json;
using AgriForecast.Infrastructure.ExternalSources.DTOs;

namespace AgriForecast.Infrastructure.ExternalSources.Interfaces;

public sealed class OpenMeteoClient : IWeatherClient
{
    private readonly HttpClient _httpClient;

    public OpenMeteoClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<DailyWeather>> GetDailyAsync(double lat, double lon, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var path = $"v1/archive?latitude={lat.ToString(inv)}&longitude={lon.ToString(inv)}" +
                   $"&start_date={from:yyyy-MM-dd}&end_date={to:yyyy-MM-dd}" +
                   $"&daily=temperature_2m_mean,precipitation_sum&timezone=auto";

        using var resp = await _httpClient.GetAsync(path, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            var preview = body.Length > 300 ? body[..300] : body;
            throw new InvalidOperationException($"Open-Meteo request failed ({(int)resp.StatusCode}). {preview}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("daily", out var daily))
            return Array.Empty<DailyWeather>();

        var times = daily.GetProperty("time");
        var temps = daily.GetProperty("temperature_2m_mean");
        var rains = daily.GetProperty("precipitation_sum");

        var result = new List<DailyWeather>(times.GetArrayLength());
        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            if (!DateOnly.TryParse(times[i].GetString(), out var date))
                continue;

            decimal? temp = temps[i].ValueKind == JsonValueKind.Number ? temps[i].GetDecimal() : null;
            var rain = rains[i].ValueKind == JsonValueKind.Number ? rains[i].GetDecimal() : 0m;
            result.Add(new DailyWeather(date, temp, rain));
        }
        return result;
    }
}
