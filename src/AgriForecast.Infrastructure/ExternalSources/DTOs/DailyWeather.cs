namespace AgriForecast.Infrastructure.ExternalSources.DTOs;

// Provider-agnostic daily weather reading (normalized from whichever source).
public sealed record DailyWeather(DateOnly Date, decimal? AvgTempC, decimal RainfallMm);
