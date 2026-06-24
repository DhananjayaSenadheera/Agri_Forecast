using System.Globalization;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.ExternalSources.DTOs;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.WeatherIngestion;

public class WeatherIngestionService : IWeatherIngestionService
{
    private readonly IWeatherClient _client;
    private readonly IConfiguration _config;
    private readonly ILogger<WeatherIngestionService> _logger;
    private readonly IWeatherRecordRepository _weatherRepo;
    private readonly IUnitofWorkRepository _unitOfWork;

    public WeatherIngestionService(IWeatherClient client, IConfiguration config,
        ILogger<WeatherIngestionService> logger, IWeatherRecordRepository weatherRepo,
        IUnitofWorkRepository unitOfWork)
    {
        _client = client;
        _config = config;
        _logger = logger;
        _weatherRepo = weatherRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task IngestAsync(CancellationToken ct)
    {
        var lat = ParseDouble(_config["WeatherSource:OpenMeteo:Latitude"] ?? _config["WeatherSource:OpenWeather:Latitude"], 7.8742);   // Dambulla
        var lon = ParseDouble(_config["WeatherSource:OpenMeteo:Longitude"] ?? _config["WeatherSource:OpenWeather:Longitude"], 80.6511);
        DateOnly.TryParse(_config["WeatherSource:StartDate"] ?? "2025-05-01", out var start);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var startMonth = new DateOnly(start.Year, start.Month, 1);
        // Only ingest fully-elapsed months; monthly aggregates are immutable once complete.
        var lastCompleteMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        if (lastCompleteMonth < startMonth)
        {
            _logger.LogInformation("Weather ingestion: no complete months to ingest yet.");
            return;
        }

        // Months already stored — skip them.
        var existing = await _weatherRepo.GetRangeAsync(
            startMonth.ToDateTime(TimeOnly.MinValue),
            lastCompleteMonth.ToDateTime(TimeOnly.MinValue), ct);
        var existingMonths = existing
            .Select(w => new DateOnly(w.Month.Year, w.Month.Month, 1))
            .ToHashSet();

        var rangeEnd = lastCompleteMonth.AddMonths(1).AddDays(-1); // last day of last complete month

        IReadOnlyList<DailyWeather> daily;
        try
        {
            daily = await _client.GetDailyAsync(lat, lon, startMonth, rangeEnd, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Weather ingestion failed while fetching daily data.");
            return;
        }

        // Group the daily readings by calendar month.
        var byMonth = daily
            .GroupBy(d => new DateOnly(d.Date.Year, d.Date.Month, 1))
            .ToDictionary(g => g.Key, g => g.ToList());

        int monthsAdded = 0, monthsSkipped = 0, monthsNoData = 0;
        for (var m = startMonth; m <= lastCompleteMonth; m = m.AddMonths(1))
        {
            if (existingMonths.Contains(m)) { monthsSkipped++; continue; }
            if (!byMonth.TryGetValue(m, out var days) || days.Count == 0) { monthsNoData++; continue; }

            var temps = days.Where(d => d.AvgTempC.HasValue).Select(d => d.AvgTempC!.Value).ToList();
            if (temps.Count == 0) { monthsNoData++; continue; }

            var avgTemp = Math.Round(temps.Average(), 2);
            var totalRainfall = Math.Round(days.Sum(d => d.RainfallMm), 2);

            var record = new WeatherRecord(new DateTime(m.Year, m.Month, 1), avgTemp, totalRainfall);
            await _weatherRepo.AddAsync(record, ct);
            await _unitOfWork.CommitAsync();
            monthsAdded++;
        }

        _logger.LogInformation(
            "Weather ingestion completed. MonthsAdded={MonthsAdded}, MonthsSkipped={MonthsSkipped}, MonthsNoData={MonthsNoData}, DaysFetched={DaysFetched}",
            monthsAdded, monthsSkipped, monthsNoData, daily.Count);
    }

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : fallback;
}
