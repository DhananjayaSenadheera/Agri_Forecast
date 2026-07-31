using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Commands.RecordSale;

/// <summary>
/// POST /api/portfolio/sales <c>{ cropId, marketId?, saleDate, pricePerKg, quantityKg?, note? }</c> — the
/// farmer types in a sale they made.
/// </summary>
/// <remarks>
/// OMITTING AN OPTIONAL KEY MEANS "I HAVE NOTHING TO SAY", which on a create is the same as sending null:
/// there is no prior value to leave alone. That is why this command needs none of the tri-state machinery
/// PUT /api/portfolio/watchlist/{cropId} carries — the distinction only exists on an update.
/// <para>
/// The nullable value types are load-bearing. <see cref="PricePerKg"/> is <c>decimal?</c> so a missing key
/// is answered with <c>invalid_price</c> rather than becoming a silent 0 that then fails a different check;
/// <see cref="SaleDate"/> is a STRING so that missing, blank and mis-spelled dates all land on the single
/// <c>invalid_sale_date</c> code instead of some of them becoming a serializer error the UI cannot switch
/// on. Both are parsed and answered in the handler, which is where every other pinned code on this
/// controller is decided.
/// </para>
/// <para>
/// UserId is stamped from the JWT and never bound from the body — see PortfolioController.
/// </para>
/// </remarks>
public class RecordSaleCommand : IRequest<Result<SaleItem_GetDto>>
{
    // Set from the JWT, never bound from the body.
    public Guid UserId { get; set; }

    /// <summary>The crop that was sold. Required, and IMMUTABLE once the row exists.</summary>
    public Guid CropId { get; set; }

    /// <summary>Where it was sold. Optional — omit it rather than guessing.</summary>
    public Guid? MarketId { get; set; }

    /// <summary>The day of the sale, <c>yyyy-MM-dd</c>. Required; anything else is <c>invalid_sale_date</c>.</summary>
    public string? SaleDate { get; set; }

    /// <summary>LKR per kilo. Required, greater than zero, at most <c>SaleLimits.MaxPricePerKg</c>.</summary>
    public decimal? PricePerKg { get; set; }

    /// <summary>
    /// Kilos sold. Optional, but when supplied it must be greater than zero and at most
    /// <c>SaleLimits.MaxQuantityKg</c> — "0 kg" is not a sale.
    /// </summary>
    public decimal? QuantityKg { get; set; }

    /// <summary>
    /// The farmer's optional note, at most <c>UserSale.NoteMaxLength</c> characters MEASURED AFTER
    /// TRIMMING. Over-long is rejected, never truncated. Stored trimmed; blank stores null.
    /// </summary>
    public string? Note { get; set; }
}
