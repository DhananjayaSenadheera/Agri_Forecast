using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Queries.GetWatchlist;

// Lists the caller's watchlist, each crop with its watched markets and planting date. Thin by design: the
// store is already owner-scoped and already ordered, so this only maps. An empty watchlist is a 200 [] —
// the "add your crops" empty state is a UI concern, not an error.
public class GetWatchlistQueryHandler
    : IRequestHandler<GetWatchlistQuery, Result<List<WatchlistItem_GetDto>>>
{
    private readonly IPortfolioReadStore _store;

    public GetWatchlistQueryHandler(IPortfolioReadStore store) => _store = store;

    public async Task<Result<List<WatchlistItem_GetDto>>> Handle(
        GetWatchlistQuery request, CancellationToken cancellationToken)
    {
        var rows = await _store.GetWatchlistAsync(request.UserId, cancellationToken);

        var items = rows.Select(WatchlistItemMapper.ToDto).ToList();

        return Result<List<WatchlistItem_GetDto>>.Success(items);
    }
}
