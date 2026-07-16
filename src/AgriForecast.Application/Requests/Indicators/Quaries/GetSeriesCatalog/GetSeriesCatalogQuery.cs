using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Indicators.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Indicators.Quaries.GetSeriesCatalog;

// GET /api/indicators/catalog. Distinct series across EconomicIndicators + MacroSeriesPoints,
// each with its kind, latest date, and row count. Feeds the admin Indicators series picker.
public class GetSeriesCatalogQuery : IRequest<Result<List<SeriesCatalog_GetDto>>>
{
}
