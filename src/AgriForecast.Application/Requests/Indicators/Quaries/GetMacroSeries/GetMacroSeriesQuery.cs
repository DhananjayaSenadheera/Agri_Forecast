using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Indicators.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Indicators.Quaries.GetMacroSeries;

// GET /api/macro-series?key=&from=&to=. Vintage-aware MacroSeriesPoints (e.g. CCPI) for one
// series. Read-only. from/to are inclusive, optional, and filter on ReferenceDate (the chart
// axis) — NEVER on PublishedAt. Both referenceDate and publishedAt are returned verbatim.
public class GetMacroSeriesQuery : IRequest<Result<List<MacroSeriesPoint_GetDto>>>
{
    // Required. The SeriesCode / seriesKey to fetch (e.g. "CCPI_BASE2021"). Blank => failure.
    public string? Key { get; set; }

    // Optional inclusive window bounds on ReferenceDate. See handler for default resolution.
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
}
