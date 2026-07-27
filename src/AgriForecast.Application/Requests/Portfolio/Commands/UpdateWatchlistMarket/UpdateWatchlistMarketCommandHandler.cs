using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistMarket;

// Sets the caller's home market, user-wide.
//
// The route names one crop, but the market is a per-farmer setting, so every row the caller owns is
// rewritten inside a single CommitAsync. Half-applied state is never observable, and the response says how
// many crops the value now covers so the UI can tell the farmer the truth about what changed.
//
// The row named in the route must belong to the CALLER. If it does not exist, or exists under another
// user, the answer is the same 404 — a 403 would confirm that the row exists for somebody else.
public class UpdateWatchlistMarketCommandHandler
    : IRequestHandler<UpdateWatchlistMarketCommand, Result<WatchlistMarketUpdate_ResultDto>>
{
    private readonly IUserCropWatchlistRepository _watchlist;
    private readonly IPortfolioReadStore _store;
    private readonly IUnitofWorkRepository _unitOfWork;
    private readonly ILogger<UpdateWatchlistMarketCommandHandler> _logger;

    public UpdateWatchlistMarketCommandHandler(
        IUserCropWatchlistRepository watchlist,
        IPortfolioReadStore store,
        IUnitofWorkRepository unitOfWork,
        ILogger<UpdateWatchlistMarketCommandHandler> logger)
    {
        _watchlist = watchlist;
        _store = store;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<WatchlistMarketUpdate_ResultDto>> Handle(
        UpdateWatchlistMarketCommand request, CancellationToken cancellationToken)
    {
        var rows = await _watchlist.GetAllForUserAsync(request.UserId, cancellationToken);

        // The load is already user-scoped, so "not in this list" covers both "no such row" and "somebody
        // else's row" without ever having looked at another user's data.
        if (rows.All(r => r.CropId != request.CropId))
        {
            _logger.LogInformation(
                "Watchlist market update rejected: user {UserId} does not watch crop {CropId}.",
                request.UserId, request.CropId);
            return Result<WatchlistMarketUpdate_ResultDto>.Failure(PortfolioErrors.WatchlistEntryNotFound);
        }

        var now = DateTime.UtcNow;

        // Every row, not just the one named in the route — that is the invariant.
        var changed = 0;
        foreach (var row in rows)
        {
            if (row.SetPreferredMarket(request.PreferredMarketId, now))
                changed++;
        }

        await _unitOfWork.CommitAsync();

        var marketName = request.PreferredMarketId.HasValue
            ? (await _store.GetMarketAsync(request.PreferredMarketId.Value, cancellationToken))?.Name
            : null;

        _logger.LogInformation(
            "Home market set for user {UserId} to {MarketId} across {CropCount} crops ({ChangedCount} rows changed).",
            request.UserId, request.PreferredMarketId, rows.Count, changed);

        return Result<WatchlistMarketUpdate_ResultDto>.Success(new WatchlistMarketUpdate_ResultDto
        {
            CropId = request.CropId,
            PreferredMarketId = request.PreferredMarketId,
            PreferredMarketName = marketName,
            // The number of crops the market now APPLIES to, not the number of rows that happened to
            // change: re-selecting the market already in force is a valid no-op, and reporting 0 there
            // would read as a failure.
            AppliedToCropCount = rows.Count
        });
    }
}
