using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Commands.RemoveWatchlistCrop;

// DELETE /api/portfolio/watchlist/{cropId} — remove a crop from the CALLER's watchlist.
//
// UserId is stamped by the controller from the JWT subject. A crop the caller does not watch is a 404,
// identically whether no such row exists or it belongs to another farmer.
public class RemoveWatchlistCropCommand : IRequest<Result<WatchlistRemove_ResultDto>>
{
    // Set from the JWT, never bound from the route — see PortfolioController.
    public Guid UserId { get; set; }

    // From the route.
    public Guid CropId { get; set; }
}
