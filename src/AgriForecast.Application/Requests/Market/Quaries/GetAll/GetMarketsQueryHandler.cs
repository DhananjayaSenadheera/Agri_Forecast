using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Market.DTOs;
using AgriForecast.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Market.Quaries.GetAll;

// Read-only market list. All business logic (the hasPrices flag threading, mapping) is
// trivial; the DB is behind IMarketReadStore so this is unit-testable. An empty result is
// a valid 200 [] (never a failure) — the admin registry and the Prices page both handle
// an empty array without erroring.
public class GetMarketsQueryHandler
    : IRequestHandler<GetMarketsQuery, Result<List<Market_GetDto>>>
{
    private readonly IMarketReadStore _store;
    private readonly ILogger<GetMarketsQueryHandler> _logger;

    public GetMarketsQueryHandler(IMarketReadStore store, ILogger<GetMarketsQueryHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<Result<List<Market_GetDto>>> Handle(
        GetMarketsQuery request, CancellationToken cancellationToken)
    {
        var rows = await _store.GetMarketsAsync(request.HasPrices, cancellationToken);

        var dtos = rows.Select(r => new Market_GetDto
        {
            Id = r.Id,
            Name = r.Name,
            District = r.District,
            MarketType = r.MarketType,
            IsEconomicCenter = r.IsEconomicCenter,
            HasStoredData = r.HasStoredData,
            LastStoredDate = r.LastStoredDate,
            IsTrainingSource = r.IsTrainingSource
        }).ToList();

        _logger.LogInformation(
            "Retrieved {MarketCount} markets (hasPrices={HasPrices}).", dtos.Count, request.HasPrices);
        return Result<List<Market_GetDto>>.Success(dtos);
    }
}
