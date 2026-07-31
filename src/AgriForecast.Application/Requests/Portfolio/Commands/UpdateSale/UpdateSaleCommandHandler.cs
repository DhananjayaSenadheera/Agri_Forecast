using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Portfolio.Commands.UpdateSale;

// Edits ONE sale of the caller's own.
//
// The load is user-scoped BY SIGNATURE (IUserSaleRepository has no by-id-alone lookup), so a row belonging
// to another farmer is simply not returned and produces the same 404 as a row that never existed. A 403
// would confirm that the id is somebody's sale.
//
// OWNERSHIP FIRST, THEN THE PAYLOAD, THEN THE MUTATION. Answering ownership before validation means a
// caller probing other people's ids learns nothing from which error comes back — every id that is not
// theirs gets the same 404 whatever they put in the body.
//
// THE CROP IS NOT TOUCHED. There is no cropId on the command and none on UserSale.Revise: a sale on the
// wrong crop is deleted and re-added, never re-pointed.
public class UpdateSaleCommandHandler : IRequestHandler<UpdateSaleCommand, Result<SaleItem_GetDto>>
{
    private readonly IUserSaleRepository _sales;
    private readonly IPortfolioReadStore _store;
    private readonly IUnitofWorkRepository _unitOfWork;
    private readonly IUserActivityAudit _activityAudit;
    private readonly ILogger<UpdateSaleCommandHandler> _logger;

    public UpdateSaleCommandHandler(
        IUserSaleRepository sales,
        IPortfolioReadStore store,
        IUnitofWorkRepository unitOfWork,
        IUserActivityAudit activityAudit,
        ILogger<UpdateSaleCommandHandler> logger)
    {
        _sales = sales;
        _store = store;
        _unitOfWork = unitOfWork;
        _activityAudit = activityAudit;
        _logger = logger;
    }

    public async Task<Result<SaleItem_GetDto>> Handle(
        UpdateSaleCommand request, CancellationToken cancellationToken)
    {
        var sale = await _sales.GetForUserAsync(request.UserId, request.SaleId, cancellationToken);
        if (sale is null)
        {
            _logger.LogInformation(
                "Sale update rejected: user {UserId} has no sale {SaleId}.",
                request.UserId, request.SaleId);
            return Result<SaleItem_GetDto>.Failure(PortfolioErrors.SaleNotFound);
        }

        var now = DateTime.UtcNow;

        var (error, payload) = SalePayload.Validate(
            request.MarketId, request.SaleDate, request.PricePerKg, request.QuantityKg, request.Note, now);

        if (error is not null)
        {
            _logger.LogInformation(
                "Sale update rejected for user {UserId}: {Error}.", request.UserId, error);
            return Result<SaleItem_GetDto>.Failure(error);
        }

        // No crop check: the crop cannot change, so the one stored on the row was already validated when it
        // was recorded and re-checking it would only be able to fail for a crop the farmer never sent.
        if (payload!.MarketId.HasValue
            && await _store.GetMarketAsync(payload.MarketId.Value, cancellationToken) is null)
        {
            _logger.LogInformation(
                "Sale update rejected for user {UserId}: unknown market {MarketId}.",
                request.UserId, payload.MarketId);
            return Result<SaleItem_GetDto>.Failure(PortfolioErrors.UnknownMarket);
        }

        // FULL REPLACE of the mutable fields — a null market, quantity or note clears it. Returns false for
        // a no-op edit, which is not an error: the farmer's row already says what they asked for, and the
        // only thing suppressed is a pointless UpdatedAtUtc churn.
        var changed = sale.Revise(
            payload.MarketId,
            payload.SaleDate,
            payload.PricePerKg,
            payload.QuantityKg,
            payload.Note,
            now);

        await _unitOfWork.CommitAsync();

        var saved = await _store.GetSaleAsync(request.UserId, request.SaleId, cancellationToken);

        // AFTER the commit, fail-open, and written even for a no-op: the farmer pressed save, and an admin
        // trail that quietly omits actions taken is not a trail. The note is not (and cannot be) passed.
        await _activityAudit.RecordSaleUpdatedAsync(
            request.UserId,
            SaleAuditDetails.RenderAuditDetails(saved?.CropCode, payload.PricePerKg, payload.SaleDate),
            cancellationToken);

        if (saved is null)
        {
            // Same reasoning as RecordSaleCommandHandler: the edit IS committed, so a 400 would be a lie
            // about what happened. Thrown, so the middleware answers 500 and records a SystemErrors row.
            _logger.LogError(
                "Sale {SaleId} was not readable after commit for user {UserId}.",
                request.SaleId, request.UserId);
            throw new InvalidOperationException(
                $"Sale {request.SaleId} was updated but could not be read back.");
        }

        _logger.LogInformation(
            "Sale updated: user {UserId}, sale {SaleId}, changed={Changed}.",
            request.UserId, request.SaleId, changed);

        return Result<SaleItem_GetDto>.Success(SaleItemMapper.ToDto(saved));
    }
}
