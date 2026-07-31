using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Commands.UpdateSale;

/// <summary>
/// PUT /api/portfolio/sales/{id} <c>{ marketId?, saleDate, pricePerKg, quantityKg?, note? }</c> — edit a
/// sale the caller already recorded.
/// </summary>
/// <remarks>
/// A TRUE PUT: the body is the sale's complete new state, so an ABSENT OPTIONAL KEY CLEARS that value.
/// Omitting <c>marketId</c>, <c>quantityKg</c> or <c>note</c> is how the farmer takes one back, and sending
/// it as null means the same thing. This is the same POST-omit-vs-PUT-null convention the rest of the
/// portfolio area uses, and it is deliberately NOT the tri-state of PUT
/// /api/portfolio/watchlist/{cropId}: there, three independent fields each needed a "leave it alone", so
/// omission had to mean something else. Here the UI always holds the whole row (it just rendered it), so a
/// full replace is both honest and simpler than a flag per field.
/// <para>
/// THERE IS NO cropId, and its absence is the enforcement. A sale recorded against the wrong crop is
/// deleted and re-added; re-pointing one would silently re-attribute a reported price to a crop the farmer
/// never named. The domain agrees — <c>UserSale.Revise</c> takes no crop id either.
/// </para>
/// <para>
/// UserId comes from the JWT and SaleId from the route, so neither can be redirected by the body. A sale
/// that does not exist, or belongs to another farmer, is the same 404 — never a 403.
/// </para>
/// </remarks>
public class UpdateSaleCommand : IRequest<Result<SaleItem_GetDto>>
{
    // Set from the JWT, never bound from the body.
    public Guid UserId { get; set; }

    // From the route.
    public Guid SaleId { get; set; }

    /// <summary>Where it was sold, or omitted/null to clear the market.</summary>
    public Guid? MarketId { get; set; }

    /// <summary>The day of the sale, <c>yyyy-MM-dd</c>. Required on every PUT — this is a full replace.</summary>
    public string? SaleDate { get; set; }

    /// <summary>LKR per kilo. Required on every PUT.</summary>
    public decimal? PricePerKg { get; set; }

    /// <summary>Kilos sold, or omitted/null to clear the quantity.</summary>
    public decimal? QuantityKg { get; set; }

    /// <summary>The farmer's note, or omitted/null to clear it. Over-long is rejected, never truncated.</summary>
    public string? Note { get; set; }
}
