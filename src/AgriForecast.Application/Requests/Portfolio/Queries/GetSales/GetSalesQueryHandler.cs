using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Queries.GetSales;

// One page of the caller's own sales. Thin by design: the owner scoping, the ordering and the count all
// live in the read store's single query, and the clamping lives on the query object, so this handler does
// nothing but map.
//
// It never sees another user's rows to filter out — the store's signature takes the caller's id, so there
// is no shape of this code in which a forgotten WHERE could leak a sale.
public class GetSalesQueryHandler : IRequestHandler<GetSalesQuery, Result<SalesPage_GetDto>>
{
    private readonly IPortfolioReadStore _store;

    public GetSalesQueryHandler(IPortfolioReadStore store) => _store = store;

    public async Task<Result<SalesPage_GetDto>> Handle(
        GetSalesQuery request, CancellationToken cancellationToken)
    {
        var page = request.EffectivePage;
        var pageSize = request.EffectivePageSize;

        var result = await _store.GetSalesPageAsync(
            request.UserId, request.CropId, page, pageSize, cancellationToken);

        return Result<SalesPage_GetDto>.Success(new SalesPage_GetDto
        {
            Items = result.Items.Select(SaleItemMapper.ToDto).ToList(),
            // The CLAMPED values are echoed, not what the caller asked for: a client that requested 1000
            // and got 50 rows must be able to tell that from the response instead of assuming it has them
            // all and never paging again.
            Page = page,
            PageSize = pageSize,
            Total = result.Total
        });
    }
}
