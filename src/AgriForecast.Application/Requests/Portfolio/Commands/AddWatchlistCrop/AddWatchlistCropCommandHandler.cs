using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;

// Adds a crop to the caller's watchlist, with 0..MaxMarketsPerCrop markets to watch it at.
//
// IDEMPOTENT: a crop already on the list is a 200 with AlreadyPresent = true, not a 409. "Watch this crop"
// is set membership, and a double-tap on a slow connection is the user asking for a state they already
// have, not an error worth interrupting them with. A repeat add carrying markets ADDS those markets to the
// existing entry (insert-only, capped) rather than replacing the set — replacing is what PUT is for.
//
// IDEMPOTENT UNDER A TRUE RACE TOO. The pre-check is a read-then-write, so two genuinely concurrent POSTs
// can both see "not present" and both insert; the second one loses to UX_UserCropWatchlist_UserCrop. That
// commit failure is caught and answered by re-reading: if the row is there now, the caller got what they
// asked for and gets the same 200 the sequential double-tap gets. A 500 (plus a SystemErrors row) for a
// double-tapped button would be a self-inflicted error report about a state the user successfully reached.
//
// THE CAPS ARE ANSWERED HERE, NOT BY THE DATABASE: the 11th crop is watchlist_full and a 4th market is
// too_many_markets, both 422 — a well-formed request the product refuses, which the farmer can act on.
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
        // Counted after de-duplication, so asking for the same market twice is not what trips the cap.
        var requestedMarkets = (request.MarketIds ?? new List<Guid>()).Distinct().ToList();

        if (requestedMarkets.Count > WatchlistLimits.MaxMarketsPerCrop)
        {
            _logger.LogInformation(
                "Watchlist add rejected for user {UserId}: {MarketCount} markets requested, cap is {Cap}.",
                request.UserId, requestedMarkets.Count, WatchlistLimits.MaxMarketsPerCrop);
            return Result<WatchlistAdd_ResultDto>.Failure(PortfolioErrors.TooManyMarkets);
        }

        var rows = await _watchlist.GetAllForUserAsync(request.UserId, cancellationToken);
        var existing = rows.FirstOrDefault(r => r.CropId == request.CropId);

        // The cap applies to NEW crops only. A repeat add of a crop already watched is idempotent, so it
        // must keep answering 200 even for a farmer sitting exactly on the limit.
        if (existing is null && rows.Count >= WatchlistLimits.MaxCropsPerUser)
        {
            _logger.LogInformation(
                "Watchlist add rejected for user {UserId}: already watching {CropCount} crops (cap {Cap}).",
                request.UserId, rows.Count, WatchlistLimits.MaxCropsPerUser);
            return Result<WatchlistAdd_ResultDto>.Failure(PortfolioErrors.WatchlistFull);
        }

        var now = DateTime.UtcNow;

        UserCropWatchlist? inserted = null;
        var target = existing;

        if (existing is null)
        {
            inserted = UserCropWatchlist.Create(request.UserId, request.CropId, plantedDate: null, now);
            await _watchlist.AddAsync(inserted, cancellationToken);
            target = inserted;
        }

        // Insert-only in BOTH branches. On a new row it is simply the initial set; on a repeat add it adds
        // what is missing without touching what the farmer already chose — a POST must never take a market
        // away, and the cap silently truncates rather than failing a request the caller already satisfied.
        await AttachMarketsAsync(target!, requestedMarkets, now, cancellationToken);

        var alreadyPresent = existing is not null;

        try
        {
            await _unitOfWork.CommitAsync();
        }
        catch (Exception ex) when (inserted is not null && ex is not OperationCanceledException)
        {
            // The only way an insert of a validated crop can fail is the (UserId, CropId) unique index,
            // i.e. a concurrent POST of the same crop got there first. The exception TYPE is deliberately
            // not inspected: the application layer does not reference EF Core or a database provider, so
            // the honest test is the question the caller actually cares about — is the row there now? If it
            // is not, this was a real failure and it is rethrown untouched.
            _watchlist.Remove(inserted); // an Added entity is detached by Remove, undoing the lost insert

            rows = await _watchlist.GetAllForUserAsync(request.UserId, cancellationToken);
            var winner = rows.FirstOrDefault(r => r.CropId == request.CropId);
            if (winner is null)
                throw;

            _logger.LogWarning(
                ex,
                "Concurrent watchlist add for user {UserId}, crop {CropId}: the row already existed, "
                + "answering idempotently.",
                request.UserId, request.CropId);

            // The failed commit rolled our child inserts back with it, so re-apply them over the row as it
            // now stands — the WINNER's row, which may already carry markets the other request inserted.
            // INSERT-ONLY: a full replace here would delete the winner's markets, i.e. one tap of a button
            // silently undoing the other. Nothing is deleted and nothing is updated on this path, so the
            // second commit cannot collide with the unique index either.
            await AttachMarketsAsync(winner, requestedMarkets, now, cancellationToken);
            await _unitOfWork.CommitAsync();

            alreadyPresent = true;
        }

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
            "Watchlist add: user {UserId}, crop {CropId}, markets {MarketCount}, alreadyPresent={AlreadyPresent}.",
            request.UserId, request.CropId, saved.Markets.Count, alreadyPresent);

        return Result<WatchlistAdd_ResultDto>.Success(new WatchlistAdd_ResultDto
        {
            AlreadyPresent = alreadyPresent,
            Item = WatchlistItemMapper.ToDto(saved)
        });
    }

    // Attaches the markets that are not attached yet and persists exactly those inserts. The entity is the
    // one that enforces the cap and the no-duplicates rule; the repository is told the insert set
    // explicitly so the write is readable here rather than implied by change tracking.
    private async Task AttachMarketsAsync(
        UserCropWatchlist entry, IReadOnlyList<Guid> marketIds, DateTime now, CancellationToken ct)
    {
        if (marketIds.Count == 0)
            return;

        var added = entry.AddMarkets(marketIds, now);
        if (added.Count > 0)
            await _watchlist.AddMarketsAsync(added, ct);
    }
}
