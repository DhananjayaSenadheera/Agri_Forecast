using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Indicators.DTOs;
using AgriForecast.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Indicators.Quaries.GetIndicatorSeries;

// Daily EconomicIndicators series. Owns input validation and default-window resolution; the DB is behind
// IIndicatorReadStore.
// Default window: to = request.To or the series' latest Date (anchored on real data, not "today"); from
// = request.From or 364 days before that. An absent To on a series with no rows returns 200 []. Only an
// explicit from > to pair is rejected.
public class GetIndicatorSeriesQueryHandler
    : IRequestHandler<GetIndicatorSeriesQuery, Result<List<DailyIndicatorPoint_GetDto>>>
{
    private const int DefaultWindowDays = 365;

    private readonly IIndicatorReadStore _store;
    private readonly ILogger<GetIndicatorSeriesQueryHandler> _logger;

    public GetIndicatorSeriesQueryHandler(
        IIndicatorReadStore store, ILogger<GetIndicatorSeriesQueryHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<Result<List<DailyIndicatorPoint_GetDto>>> Handle(
        GetIndicatorSeriesQuery request, CancellationToken cancellationToken)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code))
            return Result<List<DailyIndicatorPoint_GetDto>>.Failure(
                "code is required (use /api/indicators/catalog to list available series)");

        // Reject only an explicit inverted pair; a defaulted bound never triggers this.
        if (request.From.HasValue && request.To.HasValue && request.From.Value > request.To.Value)
            return Result<List<DailyIndicatorPoint_GetDto>>.Failure("from must be on or before to");

        var to = request.To;
        if (to is null)
        {
            var latest = await _store.GetLatestIndicatorDateAsync(code, cancellationToken);
            if (latest is null)
            {
                _logger.LogInformation("Indicator series {Code} has no data.", code);
                return Result<List<DailyIndicatorPoint_GetDto>>.Success(new List<DailyIndicatorPoint_GetDto>());
            }
            to = latest;
        }

        var toDate = to.Value;
        var fromDate = request.From ?? toDate.AddDays(-(DefaultWindowDays - 1));

        var rows = await _store.GetIndicatorRowsAsync(code, fromDate, toDate, cancellationToken);

        var points = rows
            .Select(r => new DailyIndicatorPoint_GetDto
            {
                Date = Fmt(r.Date),
                IndicatorCode = r.IndicatorCode,
                Value = r.Value,
                Source = r.Source
            })
            .ToList();

        return Result<List<DailyIndicatorPoint_GetDto>>.Success(points);
    }

    private static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
