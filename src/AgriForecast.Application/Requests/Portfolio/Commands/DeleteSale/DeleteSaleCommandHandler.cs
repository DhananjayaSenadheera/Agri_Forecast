using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Portfolio.Commands.DeleteSale;

// Deletes ONE sale of the caller's own.
//
// The load is user-scoped by signature, so another farmer's row is not in reach and answers the same 404 as
// an id that never existed. A second delete of the same id answers 404 too — there is no way to say "you
// already deleted that" without confirming the id once existed.
//
// THE AUDIT DETAILS ARE READ BEFORE THE DELETE, because after the commit there is nothing left to render
// from. That read is the only reason the row's crop code and price survive into the admin trail at all; the
// farmer's note is not read, and could not be rendered even if it were.
public class DeleteSaleCommandHandler : IRequestHandler<DeleteSaleCommand, Result<SaleDelete_ResultDto>>
{
    private readonly IUserSaleRepository _sales;
    private readonly IPortfolioReadStore _store;
    private readonly IUnitofWorkRepository _unitOfWork;
    private readonly IUserActivityAudit _activityAudit;
    private readonly ILogger<DeleteSaleCommandHandler> _logger;

    public DeleteSaleCommandHandler(
        IUserSaleRepository sales,
        IPortfolioReadStore store,
        IUnitofWorkRepository unitOfWork,
        IUserActivityAudit activityAudit,
        ILogger<DeleteSaleCommandHandler> logger)
    {
        _sales = sales;
        _store = store;
        _unitOfWork = unitOfWork;
        _activityAudit = activityAudit;
        _logger = logger;
    }

    public async Task<Result<SaleDelete_ResultDto>> Handle(
        DeleteSaleCommand request, CancellationToken cancellationToken)
    {
        var sale = await _sales.GetForUserAsync(request.UserId, request.SaleId, cancellationToken);
        if (sale is null)
        {
            _logger.LogInformation(
                "Sale delete rejected: user {UserId} has no sale {SaleId}.",
                request.UserId, request.SaleId);
            return Result<SaleDelete_ResultDto>.Failure(PortfolioErrors.SaleNotFound);
        }

        // Read the display fields while the row still exists — the crop code lives on Crops, not on the
        // sale, and after the commit there is no owner-scoped row left to join from.
        var doomed = await _store.GetSaleAsync(request.UserId, request.SaleId, cancellationToken);

        _sales.Remove(sale);
        await _unitOfWork.CommitAsync();

        // AFTER the commit and fail-open, like every other audit call. The values come from the pre-delete
        // read; the sale's own fields are used as the fallback so a failed read-back costs a crop code, not
        // the whole line.
        await _activityAudit.RecordSaleDeletedAsync(
            request.UserId,
            SaleAuditDetails.RenderAuditDetails(doomed?.CropCode, sale.PricePerKg, sale.SaleDate),
            cancellationToken);

        _logger.LogInformation(
            "Sale deleted: user {UserId}, sale {SaleId}.", request.UserId, request.SaleId);

        return Result<SaleDelete_ResultDto>.Success(new SaleDelete_ResultDto
        {
            SaleId = request.SaleId,
            Removed = true
        });
    }
}
