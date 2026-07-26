using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Indicators.DTOs;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Indicators.Quaries.GetSeriesCatalog;

// Series directory for the admin Indicators picker. The store does the per-series grouping across both
// tables; this handler only shapes and orders the result. An empty DB is a 200 [], never a failure.
public class GetSeriesCatalogQueryHandler
    : IRequestHandler<GetSeriesCatalogQuery, Result<List<SeriesCatalog_GetDto>>>
{
    private readonly IIndicatorReadStore _store;

    public GetSeriesCatalogQueryHandler(IIndicatorReadStore store) => _store = store;

    public async Task<Result<List<SeriesCatalog_GetDto>>> Handle(
        GetSeriesCatalogQuery request, CancellationToken cancellationToken)
    {
        var rows = await _store.GetCatalogAsync(cancellationToken);

        var catalog = rows
            .Select(r => new SeriesCatalog_GetDto
            {
                Key = r.Key,
                Kind = r.Kind,
                LatestDate = r.LatestDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Count = r.Count
            })
            // Deterministic: "indicator" before "macro" (alphabetical on Kind), then by Key.
            .OrderBy(c => c.Kind, StringComparer.Ordinal)
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();

        return Result<List<SeriesCatalog_GetDto>>.Success(catalog);
    }
}
