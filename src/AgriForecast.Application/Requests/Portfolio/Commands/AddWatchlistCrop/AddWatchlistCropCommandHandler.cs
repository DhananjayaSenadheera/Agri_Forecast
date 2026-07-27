using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;

// Adds a crop to the caller's watchlist.
//
// IDEMPOTENT: a crop already on the list is a 200 with AlreadyPresent = true, not a 409. "Watch this crop"
// is set membership, and a double-tap on a slow connection is the user asking for a state they already
// have, not an error worth interrupting them with.
//
// HOME-MARKET INVARIANT: one market per farmer. A supplied preferredMarketId is applied to every row the
// caller owns, and the new row inherits the market the existing rows already carry. Everything happens in
// one CommitAsync, so a partial application is not observable.
public class AddWatchlistCropCommandHandler
    : IRequestHandler<AddWatchlistCropCommand, Result<WatchlistAdd_ResultDto>>
{
    private readonly IUserCropWatchlistRepository _watchlist;
    private readonly IPortfolioReadStore _store;
    private readonly IUnitofWorkRepository _unitOfWork;
    private readonly ILogger<AddWatchlistCropCommandHandler> _logger;

    public AddWatchlistCropCommandHandler(
        IUserCropWatchlistRepository watchlist,
        IPortfolioReadStore store,
        IUnitofWorkRepository unitOfWork,
        ILogger<AddWatchlistCropCommandHandler> logger)
    {
        _watchlist = watchlist;
        _store = store;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<WatchlistAdd_ResultDto>> Handle(
        AddWatchlistCropCommand request, CancellationToken cancellationToken)
    {
        var rows = await _watchlist.GetAllForUserAsync(request.UserId, cancellationToken);
        var existing = rows.FirstOrDefault(r => r.CropId == request.CropId);

        var now = DateTime.UtcNow;

        // Null/absent means INHERIT the market the caller already uses, never "clear it".
        var effectiveMarketId = request.PreferredMarketId
            ?? HomeMarket.Resolve(rows.Select(r =>
                new HomeMarketCandidate(r.CropId, r.PreferredMarketId, r.UpdatedAtUtc)));

        if (existing is null)
        {
            await _watchlist.AddAsync(
                UserCropWatchlist.Create(request.UserId, request.CropId, effectiveMarketId, now),
                cancellationToken);
        }

        // Only an EXPLICIT market choice propagates. An inherited value is by definition what the other
        // rows already hold, so re-stamping them would churn UpdatedAtUtc for nothing.
        if (request.PreferredMarketId.HasValue)
        {
            foreach (var row in rows)
                row.SetPreferredMarket(request.PreferredMarketId, now);
        }

        await _unitOfWork.CommitAsync();

        // Read the row back through the owner-scoped store so the response carries the same crop and
        // market names the list endpoint would return.
        var saved = (await _store.GetWatchlistAsync(request.UserId, cancellationToken))
            .FirstOrDefault(r => r.CropId == request.CropId);

        if (saved is null)
        {
            _logger.LogError(
                "Watchlist row for crop {CropId} was not readable after commit for user {UserId}.",
                request.CropId, request.UserId);
            return Result<WatchlistAdd_ResultDto>.Failure("The watchlist entry could not be read back.");
        }

        _logger.LogInformation(
            "Watchlist add: user {UserId}, crop {CropId}, alreadyPresent={AlreadyPresent}.",
            request.UserId, request.CropId, existing is not null);

        return Result<WatchlistAdd_ResultDto>.Success(new WatchlistAdd_ResultDto
        {
            AlreadyPresent = existing is not null,
            Item = new WatchlistItem_GetDto
            {
                CropId = saved.CropId,
                CropName = saved.CropName,
                CropCode = saved.CropCode,
                PreferredMarketId = saved.PreferredMarketId,
                PreferredMarketName = saved.PreferredMarketName,
                CreatedAtUtc = PortfolioTime.AsUtc(saved.CreatedAtUtc)
            }
        });
    }
}
