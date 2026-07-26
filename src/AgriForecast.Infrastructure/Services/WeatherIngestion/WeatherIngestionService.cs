using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.ExternalSources.DTOs;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.WeatherIngestion;

// Fail-safe, not fail-silent: a provider outage returns Outcome=Failed on the stats rather than an early
// void return that the audit wrapper would record as a green Succeeded row (the same false-green shape
// found in NEWS). "Nothing to do yet" and "every month already stored" remain genuine successes.
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

    public async Task<IngestionRunStats> IngestAsync(CancellationToken ct)
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
            // A genuine success: monthly aggregates are only ingested once a month is fully elapsed, so
            // there is nothing to do yet. Stays green.
            _logger.LogInformation("Weather ingestion: no complete months to ingest yet.");
            return new IngestionRunStats(RowsInserted: 0);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // An admin stop, not a provider outage. Let it propagate so the audit wrapper records the
            // distinct "cancelled" reason instead of blaming the weather provider.
            throw;
        }
        catch (Exception ex)
        {
            // THE false-green path: this used to `return` and the run row went green while no weather
            // landed at all.
            _logger.LogError(ex, "Weather ingestion failed while fetching daily data.");
            return Failed("Fetching daily weather data from the provider failed.");
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

        // Months, not rows: this source's unit of work is the monthly aggregate. monthsSkipped are the
        // already-stored months, so a pass with nothing new is Succeeded with 0 inserted.
        return new IngestionRunStats(
            CoveredFromDate: startMonth,
            CoveredToDate: lastCompleteMonth,
            RowsFetched: daily.Count,
            RowsInserted: monthsAdded,
            RowsSkipped: monthsSkipped);
    }

    private static IngestionRunStats Failed(string reason) =>
        new(Outcome: IngestionRunOutcome.Failed, FailureReason: reason);

    private static double ParseDouble(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : fallback;
}
