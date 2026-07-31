using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
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
//
// CLEARING A RECORDED PLANTING DATE IS A REPORTABLE EVENT, not just a null write: it needs a reason, and the
// reason is persisted in the SAME COMMIT as the clear (see PlantedDateRemoval). Every other spelling of the
// request — setting a date, clearing an already-empty one, touching only the markets — must NOT carry a
// reason, because a reason with nothing to explain would be silently discarded.
public class UpdateWatchlistEntryCommandHandler
    : IRequestHandler<UpdateWatchlistEntryCommand, Result<WatchlistEntryUpdate_ResultDto>>
{
    private readonly IUserCropWatchlistRepository _watchlist;
    private readonly IPortfolioReadStore _store;
    private readonly IPlantedDateRemovalRepository _removals;
    private readonly IUnitofWorkRepository _unitOfWork;
    private readonly IUserActivityAudit _activityAudit;
    private readonly ILogger<UpdateWatchlistEntryCommandHandler> _logger;

    public UpdateWatchlistEntryCommandHandler(
        IUserCropWatchlistRepository watchlist,
        IPortfolioReadStore store,
        IPlantedDateRemovalRepository removals,
        IUnitofWorkRepository unitOfWork,
        IUserActivityAudit activityAudit,
        ILogger<UpdateWatchlistEntryCommandHandler> logger)
    {
        _watchlist = watchlist;
        _store = store;
        _removals = removals;
        _unitOfWork = unitOfWork;
        _activityAudit = activityAudit;
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

        // "Is this request removing a planting date the farmer actually told us about?" — the ONE predicate
        // the whole reason contract hangs off. All three clauses matter: the key must be present, its value
        // must be null, and there must be a stored date to lose. Clearing an already-null date changes
        // nothing, so it is not a removal and needs no reason.
        var removedDate = entry.PlantedDate;
        var clearingRecordedDate = request.PlantedDateSpecified
                                   && request.PlantedDate is null
                                   && removedDate.HasValue;

        var reasonGiven = !string.IsNullOrWhiteSpace(request.ClearReason);
        var noteGiven = !string.IsNullOrWhiteSpace(request.ClearReasonNote);

        if (clearingRecordedDate && !reasonGiven)
        {
            _logger.LogInformation(
                "Watchlist update rejected for user {UserId}: clearing the planting date of crop {CropId} "
                + "requires a reason.",
                request.UserId, request.CropId);
            return Result<WatchlistEntryUpdate_ResultDto>.Failure(PortfolioErrors.ClearReasonRequired);
        }

        if (reasonGiven && !clearingRecordedDate)
        {
            // Rejected rather than ignored: a caller that sends a reason believes one was recorded, and a
            // field that vanishes on the server is a contract lie regardless of how harmless it looks.
            _logger.LogInformation(
                "Watchlist update rejected for user {UserId}: a clear reason was supplied but the request "
                + "does not clear a recorded planting date for crop {CropId}.",
                request.UserId, request.CropId);
            return Result<WatchlistEntryUpdate_ResultDto>.Failure(PortfolioErrors.ClearReasonNotApplicable);
        }

        // Case-SENSITIVE parse: a differently-cased value is a client guessing at the contract, not a typo
        // worth absorbing. Only reached when a reason was actually given.
        PlantedDateRemovalReason? reason = null;
        if (reasonGiven)
        {
            reason = PlantedDateRemovalReasons.TryParse(request.ClearReason);
            if (reason is null)
            {
                _logger.LogInformation(
                    "Watchlist update rejected for user {UserId}: unknown clear reason for crop {CropId}.",
                    request.UserId, request.CropId);
                return Result<WatchlistEntryUpdate_ResultDto>.Failure(PortfolioErrors.InvalidClearReason);
            }
        }

        if (noteGiven && !reasonGiven)
        {
            _logger.LogInformation(
                "Watchlist update rejected for user {UserId}: a clear-reason note was supplied without a "
                + "reason for crop {CropId}.",
                request.UserId, request.CropId);
            return Result<WatchlistEntryUpdate_ResultDto>.Failure(
                PortfolioErrors.ClearReasonNoteWithoutReason);
        }

        // Measured on the TRIMMED note, which is what would be stored, and rejected rather than truncated:
        // shortening a farmer's own words behind their back is worse than asking them to shorten them.
        var note = request.ClearReasonNote?.Trim();
        if (note is not null && note.Length > PlantedDateRemoval.NoteMaxLength)
        {
            _logger.LogInformation(
                "Watchlist update rejected for user {UserId}: clear-reason note is {Length} characters, "
                + "cap is {Cap}.",
                request.UserId, note.Length, PlantedDateRemoval.NoteMaxLength);
            return Result<WatchlistEntryUpdate_ResultDto>.Failure(PortfolioErrors.ClearReasonNoteTooLong);
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

        // The removal row is queued BEFORE the commit, so the cleared date and the reason for clearing it
        // land in ONE transaction. This is not an audit line that may be lost: a date that disappeared with
        // no recorded reason is precisely the state this feature exists to make impossible.
        if (clearingRecordedDate)
        {
            await _removals.AddAsync(
                PlantedDateRemoval.Record(
                    request.UserId, request.CropId, removedDate!.Value, reason!.Value, note, now),
                cancellationToken);
        }

        // One commit for every part: a half-applied update is never observable.
        await _unitOfWork.CommitAsync();

        var saved = (await _store.GetWatchlistAsync(request.UserId, cancellationToken))
            .FirstOrDefault(r => r.CropId == request.CropId);

        // Audited AFTER the commit and fail-open, like every other Record* call. It is written even when the
        // read-back below fails, because the removal DID commit and an admin trail that skips committed work
        // is worse than one missing a crop code (the reason word is still there).
        if (clearingRecordedDate)
        {
            await _activityAudit.RecordPlantedDateRemovedAsync(
                request.UserId,
                PlantedDateRemovalReasons.RenderAuditDetails(saved?.CropCode, reason!.Value),
                cancellationToken);
        }

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
            + "(changed={MarketsChanged}), plantedDateChanged={PlantedDateChanged}, "
            + "plantedDateRemoved={PlantedDateRemoved}.",
            request.UserId, request.CropId, saved.Markets.Count, marketsChanged, plantedDateChanged,
            clearingRecordedDate);

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
    /// The upper bound is <see cref="PortfolioTime.LatestPlausibleLocalDate"/> — the UTC date PLUS ONE DAY
    /// — and it is read from there rather than computed here so that this rule and the sales log's
    /// <c>sale_date_future</c> rule can never disagree about which day it is. The reasoning for the day of
    /// slack (Sri Lanka is UTC+5:30, and no tzdata is assumed in the container) lives with the helper.
    /// </remarks>
    private static bool IsPlantableDate(DateOnly? plantedDate, DateTime nowUtc)
    {
        if (!plantedDate.HasValue)
            return true;

        return plantedDate.Value >= WatchlistLimits.EarliestPlantedDate
               && plantedDate.Value <= PortfolioTime.LatestPlausibleLocalDate(nowUtc);
    }
}
