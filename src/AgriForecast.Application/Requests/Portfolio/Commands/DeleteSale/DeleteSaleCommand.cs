using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Commands.DeleteSale;

/// <summary>
/// DELETE /api/portfolio/sales/{id} — the farmer removes a sale they recorded. 204 on success.
/// </summary>
/// <remarks>
/// A HARD DELETE, and that is the right answer here: this is the farmer's own self-reported note about
/// their own business, nothing downstream depends on it (the table is quarantined out of the feature and
/// training path entirely), and keeping a soft-deleted copy of data somebody asked us to forget would be
/// the opposite of what they asked for.
/// <para>
/// NOT IDEMPOTENT: a second delete is a 404, the same answer a stranger's id gets. There is no way to tell
/// "I already deleted this" from "that was never yours" without revealing which ids exist, and privacy wins.
/// </para>
/// <para>
/// UserId comes from the JWT and SaleId from the route.
/// </para>
/// </remarks>
public class DeleteSaleCommand : IRequest<Result<SaleDelete_ResultDto>>
{
    // Set from the JWT, never bound from the body or route.
    public Guid UserId { get; set; }

    // From the route.
    public Guid SaleId { get; set; }
}
