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
// The market cap is measured against what the crop would END UP following (existing ∪ requested), not
// against the request's own length. The entity's own truncation is a last resort reached only on the
// race-recovery path below, where the alternative — failing a request whose crop is already added — would
// be worse; on the ordinary path silently dropping a market the caller was told 200 about is not honest.
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

        // The crop cap applies to NEW crops only. A repeat add of a crop already watched is idempotent, so
        // it must keep answering 200 even for a farmer sitting exactly on the limit.
        if (existing is null && rows.Count >= WatchlistLimits.MaxCropsPerUser)
        {
            _logger.LogInformation(
                "Watchlist add rejected for user {UserId}: already watching {CropCount} crops (cap {Cap}).",
                request.UserId, rows.Count, WatchlistLimits.MaxCropsPerUser);
            return Result<WatchlistAdd_ResultDto>.Failure(PortfolioErrors.WatchlistFull);
        }

        // The MARKET cap must count what the crop ALREADY follows, not just what this request carries.
        // Checking the request alone let a crop already on 3 markets take a 4th: the entity truncates
        // silently and the caller got a 200 saying the market was accepted when it had been dropped. Told
        // "no" they can remove one; told nothing they never find out.
        if (existing is not null)
        {
            var wouldFollow = existing.Markets.Select(m => m.MarketId)
                .Union(requestedMarkets)
                .Count();

            if (wouldFollow > WatchlistLimits.MaxMarketsPerCrop)
            {
                _logger.LogInformation(
                    "Watchlist add rejected for user {UserId}, crop {CropId}: would follow {MarketCount} "
                    + "markets, cap is {Cap}.",
                    request.UserId, request.CropId, wouldFollow, WatchlistLimits.MaxMarketsPerCrop);
                return Result<WatchlistAdd_ResultDto>.Failure(PortfolioErrors.TooManyMarkets);
            }
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
        // away. The over-cap case was already refused above, so nothing is silently truncated here.
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
            // THIS LINE IS THE CLEAN-UP, and it is load-bearing. A failed SaveChanges does NOT roll the
            // change tracker back: our losing parent row and its child markets are still sitting there
            // Added, and the next commit would replay them straight into the same unique index. Removing an
            // Added parent DETACHES it, and the detach CASCADES to its Added children — so this one call is
            // what leaves a clean tracker for the retry below. Reorder it after the re-read and the second
            // commit resurrects the losing insert.
            _watchlist.Remove(inserted);

            rows = await _watchlist.GetAllForUserAsync(request.UserId, cancellationToken);
            var winner = rows.FirstOrDefault(r => r.CropId == request.CropId);
            if (winner is null)
                throw;

            _logger.LogWarning(
                ex,
                "Concurrent watchlist add for user {UserId}, crop {CropId}: the row already existed, "
                + "answering idempotently.",
                request.UserId, request.CropId);

            // Our child inserts went out with the detach, so re-apply them over the row as it now stands —
            // the WINNER's row, which may already carry markets the other request inserted.
            // INSERT-ONLY: a full replace here would delete the winner's markets, i.e. one tap of a button
            // silently undoing the other. Nothing is deleted and nothing is updated on this path, so the
            // second commit cannot collide with the unique index either. This is also the ONE place the
            // entity's silent truncation at the cap is the right answer: the caller's crop is already
            // there, and failing the whole request over a market count they only reached through a race
            // would be worse than honouring the first MaxMarketsPerCrop.
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
