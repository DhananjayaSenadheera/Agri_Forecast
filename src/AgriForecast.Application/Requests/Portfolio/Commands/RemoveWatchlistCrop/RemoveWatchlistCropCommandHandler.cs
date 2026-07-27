using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Portfolio.Commands.RemoveWatchlistCrop;

// Removes a crop from the caller's watchlist.
//
// The load is user-scoped, so a row belonging to another farmer is simply not in the set and produces the
// same 404 as a row that never existed. Removing the last crop leaves an empty watchlist, which is a valid
// state, not an error — the home market is simply forgotten with the rows and re-defaults on the next add.
public class RemoveWatchlistCropCommandHandler
    : IRequestHandler<RemoveWatchlistCropCommand, Result<WatchlistRemove_ResultDto>>
{
    private readonly IUserCropWatchlistRepository _watchlist;
    private readonly IUnitofWorkRepository _unitOfWork;
    private readonly ILogger<RemoveWatchlistCropCommandHandler> _logger;

    public RemoveWatchlistCropCommandHandler(
        IUserCropWatchlistRepository watchlist,
        IUnitofWorkRepository unitOfWork,
        ILogger<RemoveWatchlistCropCommandHandler> logger)
    {
        _watchlist = watchlist;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<WatchlistRemove_ResultDto>> Handle(
        RemoveWatchlistCropCommand request, CancellationToken cancellationToken)
    {
        var rows = await _watchlist.GetAllForUserAsync(request.UserId, cancellationToken);
        var row = rows.FirstOrDefault(r => r.CropId == request.CropId);

        if (row is null)
        {
            _logger.LogInformation(
                "Watchlist delete rejected: user {UserId} does not watch crop {CropId}.",
                request.UserId, request.CropId);
            return Result<WatchlistRemove_ResultDto>.Failure(PortfolioErrors.WatchlistEntryNotFound);
        }

        _watchlist.Remove(row);
        await _unitOfWork.CommitAsync();

        _logger.LogInformation(
            "Watchlist remove: user {UserId}, crop {CropId}.", request.UserId, request.CropId);

        return Result<WatchlistRemove_ResultDto>.Success(new WatchlistRemove_ResultDto
        {
            CropId = request.CropId,
            Removed = true
        });
    }
}
