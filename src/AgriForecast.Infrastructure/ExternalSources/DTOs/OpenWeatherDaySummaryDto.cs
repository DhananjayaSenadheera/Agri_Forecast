using System.Text.Json.Serialization;

namespace AgriForecast.Infrastructure.ExternalSources.DTOs;

// Shape of the OpenWeather One Call 3.0 "day_summary" response.
public sealed class OpenWeatherDaySummaryDto
{
    [JsonPropertyName("lat")] public double Lat { get; set; }
    [JsonPropertyName("lon")] public double Lon { get; set; }
    [JsonPropertyName("date")] public string Date { get; set; } = "";

    [JsonPropertyName("temperature")] public OpenWeatherTemperatureDto? Temperature { get; set; }
    [JsonPropertyName("precipitation")] public OpenWeatherPrecipitationDto? Precipitation { get; set; }
}

public sealed class OpenWeatherTemperatureDto
{
    [JsonPropertyName("min")] public decimal Min { get; set; }
    [JsonPropertyName("max")] public decimal Max { get; set; }
    [JsonPropertyName("afternoon")] public decimal Afternoon { get; set; }
    [JsonPropertyName("morning")] public decimal Morning { get; set; }
    [JsonPropertyName("evening")] public decimal Evening { get; set; }
    [JsonPropertyName("night")] public decimal Night { get; set; }
}

public sealed class OpenWeatherPrecipitationDto
{
    [JsonPropertyName("total")] public decimal Total { get; set; }
}
