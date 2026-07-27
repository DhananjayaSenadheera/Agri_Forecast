using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;

// POST /api/portfolio/watchlist { cropId, preferredMarketId? } — add a crop to the CALLER's watchlist.
//
// UserId is stamped by the controller from the JWT subject. It is NOT part of the request body: a farmer
// cannot add a crop to someone else's watchlist by editing the payload.
public class AddWatchlistCropCommand : IRequest<Result<WatchlistAdd_ResultDto>>
{
    // Set from the JWT, never bound from the body — see PortfolioController.
    public Guid UserId { get; set; }

    public Guid CropId { get; set; }

    /// <summary>
    /// Optional home market. When supplied it becomes the caller's home market and is applied to ALL of
    /// their watchlist rows (one home market per farmer).
    /// <para>
    /// OMITTED OR NULL MEANS "INHERIT", NOT "CLEAR". Adding a crop must never silently reset a farmer's
    /// chosen market back to the national default, and JSON cannot distinguish an absent key from an
    /// explicit null. Clearing the market back to national is what PUT /watchlist/{cropId} is for, where
    /// null is unambiguous because changing the market is the whole point of the call.
    /// </para>
    /// </summary>
    public Guid? PreferredMarketId { get; set; }
}
