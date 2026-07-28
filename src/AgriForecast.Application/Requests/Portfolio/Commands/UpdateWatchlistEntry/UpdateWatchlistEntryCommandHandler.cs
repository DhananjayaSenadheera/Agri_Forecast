using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistEntry;

// Updates ONE watched crop: its market set and/or its planting date.
//
// Per-crop, not per-farmer. This replaced the one-home-market-per-farmer rule, so a write here touches
// exactly the row named in the route and never rewrites the caller's other crops.
//
// The row must belong to the CALLER. If it does not exist, or exists under another user, the answer is the
// same 404 — a 403 would confirm that the row exists for somebody else.
public class UpdateWatchlistEntryCommandHandler
    : IRequestHandler<UpdateWatchlistEntryCommand, Result<WatchlistEntryUpdate_ResultDto>>
{
    private readonly IUserCropWatchlistRepository _watchlist;
    private readonly IPortfolioReadStore _store;
    private readonly IUnitofWorkRepository _unitOfWork;
    private readonly ILogger<UpdateWatchlistEntryCommandHandler> _logger;

    public UpdateWatchlistEntryCommandHandler(
        IUserCropWatchlistRepository watchlist,
        IPortfolioReadStore store,
        IUnitofWorkRepository unitOfWork,
        ILogger<UpdateWatchlistEntryCommandHandler> logger)
    {
        _watchlist = watchlist;
        _store = store;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }

    public async Task<Result<WatchlistEntryUpdate_ResultDto>> Handle(
        UpdateWatchlistEntryCommand request, CancellationToken cancellationToken)
    {
        var rows = await _watchlist.GetAllForUserAsync(request.UserId, cancellationToken);

        // The load is already user-scoped, so "not in this list" covers both "no such row" and "somebody
        // else's row" without ever having looked at another user's data.
        var entry = rows.FirstOrDefault(r => r.CropId == request.CropId);
        if (entry is null)
        {
            _logger.LogInformation(
                "Watchlist update rejected: user {UserId} does not watch crop {CropId}.",
                request.UserId, request.CropId);
            return Result<WatchlistEntryUpdate_ResultDto>.Failure(PortfolioErrors.WatchlistEntryNotFound);
        }

        // Validate EVERYTHING before mutating anything: a request that fails halfway must leave the entry
        // exactly as it was, not with its markets replaced and its date rejected.
        var replacing = request.MarketIds is not null;
        var desiredMarkets = (request.MarketIds ?? new List<Guid>()).Distinct().ToList();

        if (replacing && desiredMarkets.Count > WatchlistLimits.MaxMarketsPerCrop)
        {
            _logger.LogInformation(
                "Watchlist update rejected for user {UserId}: {MarketCount} markets requested, cap is {Cap}.",
                request.UserId, desiredMarkets.Count, WatchlistLimits.MaxMarketsPerCrop);
            return Result<WatchlistEntryUpdate_ResultDto>.Failure(PortfolioErrors.TooManyMarkets);
        }

        var now = DateTime.UtcNow;

        if (request.PlantedDateSpecified && !IsPlantableDate(request.PlantedDate, now))
        {
            _logger.LogInformation(
                "Watchlist update rejected for user {UserId}: planting date {PlantedDate} is out of range.",
                request.UserId, request.PlantedDate);
            return Result<WatchlistEntryUpdate_ResultDto>.Failure(PortfolioErrors.InvalidPlantedDate);
        }

        var marketsChanged = false;
        if (replacing)
        {
            // FULL REPLACE — the request carries the complete set, so anything not in it goes. An empty
            // array is a deliberate "clear my markets", not an accident: omitting the field is how a
            // caller says "leave them alone", and the two spellings are different requests.
            var changes = entry.ReplaceMarkets(desiredMarkets, now);

            if (changes.Added.Count > 0)
                await _watchlist.AddMarketsAsync(changes.Added, cancellationToken);

            if (changes.Removed.Count > 0)
                _watchlist.RemoveMarkets(changes.Removed);

            marketsChanged = changes.Added.Count > 0 || changes.Removed.Count > 0;
        }

        var plantedDateChanged = request.PlantedDateSpecified
                                 && entry.SetPlantedDate(request.PlantedDate, now);

        // One commit for both halves: a half-applied update is never observable.
        await _unitOfWork.CommitAsync();

        var saved = (await _store.GetWatchlistAsync(request.UserId, cancellationToken))
            .FirstOrDefault(r => r.CropId == request.CropId);

        if (saved is null)
        {
            _logger.LogError(
                "Watchlist row for crop {CropId} was not readable after commit for user {UserId}.",
                request.CropId, request.UserId);
            return Result<WatchlistEntryUpdate_ResultDto>.Failure(
                "The watchlist entry could not be read back.");
        }

        _logger.LogInformation(
            "Watchlist update: user {UserId}, crop {CropId}, markets {MarketCount} "
            + "(changed={MarketsChanged}), plantedDateChanged={PlantedDateChanged}.",
            request.UserId, request.CropId, saved.Markets.Count, marketsChanged, plantedDateChanged);

        return Result<WatchlistEntryUpdate_ResultDto>.Success(new WatchlistEntryUpdate_ResultDto
        {
            Item = WatchlistItemMapper.ToDto(saved),
            MarketsChanged = marketsChanged,
            PlantedDateChanged = plantedDateChanged
        });
    }

    /// <summary>
    /// Null (clear) is always allowed. A real date must sit between
    /// <see cref="WatchlistLimits.EarliestPlantedDate"/> and the caller's plausible "today".
    /// </summary>
    /// <remarks>
    /// The upper bound is the UTC date PLUS ONE DAY, not the UTC date. Sri Lanka is UTC+5:30, so between
    /// 18:30 and 24:00 UTC a farmer's local "today" is already the next calendar day; a strict UTC cutoff
    /// would reject the honest answer of anyone planting during their own evening. One day of slack fixes
    /// that without letting a genuine future date through — nobody plants next week by accident, and the
    /// error a farmer actually makes (a mis-keyed year) is caught by either bound.
    /// <para>
    /// The zone is not consulted directly on purpose: TimeZoneInfo.FindSystemTimeZoneById needs a tz
    /// database in the container, and a validation rule that throws when the image ships without tzdata
    /// would be a worse failure than a day of tolerance.
    /// </para>
    /// </remarks>
    private static bool IsPlantableDate(DateOnly? plantedDate, DateTime nowUtc)
    {
        if (!plantedDate.HasValue)
            return true;

        var latestAllowed = DateOnly.FromDateTime(nowUtc).AddDays(1);

        return plantedDate.Value >= WatchlistLimits.EarliestPlantedDate
               && plantedDate.Value <= latestAllowed;
    }
}
