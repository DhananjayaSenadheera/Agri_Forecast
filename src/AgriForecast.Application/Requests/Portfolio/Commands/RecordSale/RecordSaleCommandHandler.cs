using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Portfolio.Commands.RecordSale;

// Records one sale the farmer typed in.
//
// NOT IDEMPOTENT, deliberately unlike the watchlist add. Two sales of the same crop at the same price on
// the same day is an ordinary thing to have happened (two buyers, two lots), so there is no unique index
// and no "already present" answer — collapsing them would delete a real row of the farmer's own history.
//
// VALIDATION IS COMPLETE BEFORE ANYTHING IS TOUCHED: the payload rules (SalePayload.Validate) and then the
// two existence lookups, all of them answered with pinned wire codes, before a single field is written.
// One CommitAsync; the audit line goes out AFTER it and fail-open.
public class RecordSaleCommandHandler : IRequestHandler<RecordSaleCommand, Result<SaleItem_GetDto>>
{
    private readonly IUserSaleRepository _sales;
    private readonly IPortfolioReadStore _store;
    private readonly IUnitofWorkRepository _unitOfWork;
    private readonly IUserActivityAudit _activityAudit;
    private readonly ILogger<RecordSaleCommandHandler> _logger;

    public RecordSaleCommandHandler(
        IUserSaleRepository sales,
        IPortfolioReadStore store,
        IUnitofWorkRepository unitOfWork,
        IUserActivityAudit activityAudit,
        ILogger<RecordSaleCommandHandler> logger)
    {
        _sales = sales;
        _store = store;
        _unitOfWork = unitOfWork;
        _activityAudit = activityAudit;
        _logger = logger;
    }

    public async Task<Result<SaleItem_GetDto>> Handle(
        RecordSaleCommand request, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;

        var (error, payload) = SalePayload.Validate(
            request.MarketId, request.SaleDate, request.PricePerKg, request.QuantityKg, request.Note, now);

        if (error is not null)
        {
            // The VALUE is never logged — a rejected price is still the farmer's business. The code says
            // which rule fired, which is all an operator needs.
            _logger.LogInformation(
                "Sale record rejected for user {UserId}: {Error}.", request.UserId, error);
            return Result<SaleItem_GetDto>.Failure(error);
        }

        // The reference lookups run last of the checks: they cost a query each, and there is no point
        // spending them on a payload that was already malformed. A 400 with a code (not a 404) matches how
        // POST /api/portfolio/watchlist answers an unknown crop id — the row being created is not what is
        // missing, a value inside the payload is.
        if (!await _store.CropExistsAsync(request.CropId, cancellationToken))
        {
            _logger.LogInformation(
                "Sale record rejected for user {UserId}: unknown crop {CropId}.",
                request.UserId, request.CropId);
            return Result<SaleItem_GetDto>.Failure(PortfolioErrors.UnknownCrop);
        }

        if (payload!.MarketId.HasValue
            && await _store.GetMarketAsync(payload.MarketId.Value, cancellationToken) is null)
        {
            _logger.LogInformation(
                "Sale record rejected for user {UserId}: unknown market {MarketId}.",
                request.UserId, payload.MarketId);
            return Result<SaleItem_GetDto>.Failure(PortfolioErrors.UnknownMarket);
        }

        var sale = UserSale.Record(
            request.UserId,
            request.CropId,
            payload.MarketId,
            payload.SaleDate,
            payload.PricePerKg,
            payload.QuantityKg,
            payload.Note,
            now);

        await _sales.AddAsync(sale, cancellationToken);
        await _unitOfWork.CommitAsync();

        // Read back through the OWNER-SCOPED store so the response carries the same crop and market names
        // the list endpoint returns — one shape for the row, whichever endpoint produced it.
        var saved = await _store.GetSaleAsync(request.UserId, sale.Id, cancellationToken);

        // Audited AFTER the commit and fail-open, like every other Record* call, and written even when the
        // read-back failed: the sale DID commit, and an admin trail that skips committed work is worse than
        // one missing a crop code. The farmer's note is not passed — it cannot be (see SaleAuditDetails).
        await _activityAudit.RecordSaleRecordedAsync(
            request.UserId,
            SaleAuditDetails.RenderAuditDetails(saved?.CropCode, payload.PricePerKg, payload.SaleDate),
            cancellationToken);

        if (saved is null)
        {
            // THROWN, NOT RETURNED AS A FAILURE. The row IS committed: answering 400 would tell the farmer
            // their sale was rejected when it was saved, and they would type it again. This is a server
            // fault — the middleware turns it into a 500 with a SystemErrors row and no leaked stack — and
            // a 500 after a successful write is at least honest about which side is broken.
            _logger.LogError(
                "Sale {SaleId} was not readable after commit for user {UserId}.", sale.Id, request.UserId);
            throw new InvalidOperationException(
                $"Sale {sale.Id} was committed but could not be read back.");
        }

        _logger.LogInformation(
            "Sale recorded: user {UserId}, sale {SaleId}, crop {CropId}, date {SaleDate}.",
            request.UserId, sale.Id, request.CropId, payload.SaleDate);

        return Result<SaleItem_GetDto>.Success(SaleItemMapper.ToDto(saved));
    }
}
