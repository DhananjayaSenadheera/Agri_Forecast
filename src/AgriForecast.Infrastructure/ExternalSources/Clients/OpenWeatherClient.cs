using System.Globalization;
using System.Text.Json;
using AgriForecast.Infrastructure.ExternalSources.DTOs;
using Microsoft.Extensions.Configuration;

namespace AgriForecast.Infrastructure.ExternalSources.Interfaces;

// OpenWeather One Call 3.0 "day_summary" (historical) — requires the paid
// "One Call by Call" subscription. Kept as a swappable provider; select via
// WeatherSource:Provider = "OpenWeather".
public sealed class OpenWeatherClient : IWeatherClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public OpenWeatherClient(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _apiKey = configuration["WeatherSource:OpenWeather:ApiKey"] ?? "";
    }

    public async Task<IReadOnlyList<DailyWeather>> GetDailyAsync(double lat, double lon, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException("Missing WeatherSource:OpenWeather:ApiKey");

        var inv = CultureInfo.InvariantCulture;
        var result = new List<DailyWeather>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var path = $"data/3.0/onecall/day_summary?lat={lat.ToString(inv)}&lon={lon.ToString(inv)}" +
                       $"&date={date:yyyy-MM-dd}&units=metric&appid={_apiKey}";

            using var resp = await _httpClient.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
                continue;

            var raw = await resp.Content.ReadAsStringAsync(ct);
            var dto = JsonSerializer.Deserialize<OpenWeatherDaySummaryDto>(
                raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (dto?.Temperature is null)
                continue;

            var avg = (dto.Temperature.Min + dto.Temperature.Max) / 2m;
            result.Add(new DailyWeather(date, avg, dto.Precipitation?.Total ?? 0m));
        }
        return result;
    }
}
