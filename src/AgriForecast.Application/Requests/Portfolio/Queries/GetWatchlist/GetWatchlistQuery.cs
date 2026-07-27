using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Queries.GetWatchlist;

// GET /api/portfolio/watchlist — the CALLER's watched crops, ordered by crop name.
//
// UserId is stamped by the controller from the JWT subject and is never accepted from the query string,
// route or body. It is on the query object only because the handler needs it; there is no route or model
// binder that can populate it, so a caller cannot ask for someone else's watchlist.
public class GetWatchlistQuery : IRequest<Result<List<WatchlistItem_GetDto>>>
{
    public Guid UserId { get; set; }
}
