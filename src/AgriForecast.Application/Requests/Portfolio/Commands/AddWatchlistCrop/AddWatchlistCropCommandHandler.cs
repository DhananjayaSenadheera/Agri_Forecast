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
// IDEMPOTENT UNDER A TRUE RACE TOO. The pre-check is a read-then-write, so two genuinely concurrent POSTs
// can both see "not present" and both insert; the second one loses to UX_UserCropWatchlist_UserCrop. That
// commit failure is caught and answered by re-reading: if the row is there now, the caller got what they
// asked for and gets the same 200 the sequential double-tap gets. A 500 (plus a SystemErrors row) for a
// double-tapped button would be a self-inflicted error report about a state the user successfully reached.
//
// HOME-MARKET INVARIANT: one market per farmer. The effective market — the caller's explicit choice, or
// the one their existing rows already carry — is applied to EVERY row they own, so rows that somehow
// disagree (a PUT interleaved with this add) converge instead of leaving the home market to be resolved by
// timestamp. Rows already holding the value are not re-stamped (SetPreferredMarket no-ops on an equal
// value), so this costs no UpdatedAtUtc churn. Everything happens in one CommitAsync, so a partial
// application is not observable.
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

        UserCropWatchlist? inserted = null;

        if (existing is null)
        {
            inserted = UserCropWatchlist.Create(request.UserId, request.CropId, effectiveMarketId, now);
            await _watchlist.AddAsync(inserted, cancellationToken);
        }

        ApplyHomeMarket(rows, effectiveMarketId, now);

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
            if (rows.All(r => r.CropId != request.CropId))
                throw;

            _logger.LogWarning(
                ex,
                "Concurrent watchlist add for user {UserId}, crop {CropId}: the row already existed, "
                + "answering idempotently.",
                request.UserId, request.CropId);

            // The failed commit rolled the home-market propagation back with it, so re-apply it over the
            // rows as they now stand (which includes the winning insert) and commit that on its own. These
            // are updates to existing rows only, so the unique index cannot bite twice.
            ApplyHomeMarket(rows, effectiveMarketId, now);
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
            "Watchlist add: user {UserId}, crop {CropId}, alreadyPresent={AlreadyPresent}.",
            request.UserId, request.CropId, alreadyPresent);

        return Result<WatchlistAdd_ResultDto>.Success(new WatchlistAdd_ResultDto
        {
            AlreadyPresent = alreadyPresent,
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

    // The home-market invariant, applied to every row the CALLER owns (never anyone else's — the rows come
    // from the user-scoped repository). Rows that already hold the value are left untouched by the entity
    // itself, so an inherited market costs nothing.
    private static void ApplyHomeMarket(
        IEnumerable<UserCropWatchlist> rows, Guid? effectiveMarketId, DateTime now)
    {
        foreach (var row in rows)
            row.SetPreferredMarket(effectiveMarketId, now);
    }
}
