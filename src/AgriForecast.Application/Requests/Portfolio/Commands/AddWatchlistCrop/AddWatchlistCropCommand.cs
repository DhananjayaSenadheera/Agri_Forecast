using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;

// POST /api/portfolio/watchlist { cropId, marketIds? } — add a crop to the CALLER's watchlist.
//
// UserId is stamped by the controller from the JWT subject. It is NOT part of the request body: a farmer
// cannot add a crop to someone else's watchlist by editing the payload.
public class AddWatchlistCropCommand : IRequest<Result<WatchlistAdd_ResultDto>>
{
    // Set from the JWT, never bound from the body — see PortfolioController.
    public Guid UserId { get; set; }

    public Guid CropId { get; set; }

    /// <summary>
    /// The markets to watch this crop at, 0 to
    /// <see cref="Domain.Constants.WatchlistLimits.MaxMarketsPerCrop"/> of them. Omitted or empty adds the
    /// crop with NO markets, which is a legitimate state read as the national / economic-centre default.
    /// <para>
    /// Duplicate ids are COLLAPSED, not rejected — a client sending the same market twice is asking for one
    /// market, not making an error worth a 4xx — and the cap is counted after the collapse, so
    /// <c>[A, A, B]</c> is two markets, not three.
    /// </para>
    /// </summary>
    public List<Guid>? MarketIds { get; set; }
}
