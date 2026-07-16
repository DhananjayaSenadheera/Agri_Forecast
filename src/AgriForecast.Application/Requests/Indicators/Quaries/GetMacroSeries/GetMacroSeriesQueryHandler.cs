using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Indicators.DTOs;
using AgriForecast.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Indicators.Quaries.GetMacroSeries;

// Vintage-aware macro series (ADM-6 / API-11). Owns input validation (key required, from<=to)
// and default-window resolution; the DB is behind IIndicatorReadStore.
//
// DEFAULT WINDOW: identical policy to GetIndicatorSeries but anchored on ReferenceDate:
//   * to   = request.To ?? the series' latest ReferenceDate. Absent To + no rows -> 200 [].
//   * from = request.From ?? to.AddDays(-(DefaultWindowDays - 1))  (last 365 inclusive days).
// The window filters ReferenceDate ONLY. An EXPLICIT from>to pair is rejected.
//
// LEAKAGE GUARD (LOAD-BEARING): the handler maps referenceDate and publishedAt onto SEPARATE
// DTO fields, verbatim from the store row. It never derives one from the other, never drops
// either, and never filters on publishedAt. A test asserts both dates survive independently.
public class GetMacroSeriesQueryHandler
    : IRequestHandler<GetMacroSeriesQuery, Result<List<MacroSeriesPoint_GetDto>>>
{
    private const int DefaultWindowDays = 365;

    private readonly IIndicatorReadStore _store;
    private readonly ILogger<GetMacroSeriesQueryHandler> _logger;

    public GetMacroSeriesQueryHandler(
        IIndicatorReadStore store, ILogger<GetMacroSeriesQueryHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<Result<List<MacroSeriesPoint_GetDto>>> Handle(
        GetMacroSeriesQuery request, CancellationToken cancellationToken)
    {
        var key = request.Key?.Trim();
        if (string.IsNullOrEmpty(key))
            return Result<List<MacroSeriesPoint_GetDto>>.Failure(
                "key is required (use /api/indicators/catalog to list available series)");

        if (request.From.HasValue && request.To.HasValue && request.From.Value > request.To.Value)
            return Result<List<MacroSeriesPoint_GetDto>>.Failure("from must be on or before to");

        var to = request.To;
        if (to is null)
        {
            var latest = await _store.GetLatestMacroReferenceDateAsync(key, cancellationToken);
            if (latest is null)
            {
                _logger.LogInformation("Macro series {Key} has no data.", key);
                return Result<List<MacroSeriesPoint_GetDto>>.Success(new List<MacroSeriesPoint_GetDto>());
            }
            to = latest;
        }

        var toDate = to.Value;
        var fromDate = request.From ?? toDate.AddDays(-(DefaultWindowDays - 1));

        var rows = await _store.GetMacroRowsAsync(key, fromDate, toDate, cancellationToken);

        var points = rows
            .Select(r => new MacroSeriesPoint_GetDto
            {
                SeriesKey = r.SeriesCode,
                // Both dates, verbatim, on their own fields — the two-date discipline.
                ReferenceDate = Fmt(r.ReferenceDate),
                PublishedAt = Fmt(r.PublishedAt),
                Value = r.Value,
                Source = r.Source
            })
            .ToList();

        return Result<List<MacroSeriesPoint_GetDto>>.Success(points);
    }

    private static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
