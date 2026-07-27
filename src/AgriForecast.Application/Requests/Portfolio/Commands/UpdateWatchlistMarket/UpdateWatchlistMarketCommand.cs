using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistMarket;

// PUT /api/portfolio/watchlist/{cropId} { preferredMarketId? } — set the CALLER's home market.
//
// The crop in the route identifies WHOSE-AND-WHICH row must exist for the call to be legitimate; the
// market change itself applies to every crop the caller watches (one home market per farmer). A crop the
// caller does not watch is a 404, whether it belongs to nobody or to another farmer.
public class UpdateWatchlistMarketCommand : IRequest<Result<WatchlistMarketUpdate_ResultDto>>
{
    // Set from the JWT, never bound from the body or route — see PortfolioController.
    public Guid UserId { get; set; }

    // From the route.
    public Guid CropId { get; set; }

    /// <summary>
    /// The home market to use, or null to clear it back to the national / economic-centre default.
    /// <para>
    /// Unlike POST, null here is MEANINGFUL and is applied: this endpoint exists to change the market, so
    /// "no market" is a choice the farmer is making, not an omission.
    /// </para>
    /// </summary>
    public Guid? PreferredMarketId { get; set; }
}
