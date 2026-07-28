using AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistEntry;
using AgriForecast.Application.Services;
using FluentValidation;

namespace AgriForecast.Application.Requests.Portfolio.Validators;

// PUT /api/portfolio/watchlist/{cropId}.
//
// Markets must exist WHEN SUPPLIED. An omitted marketIds is not a missing field — it means "leave the
// markets alone" — and an EMPTY array is a deliberate "clear them", so neither is treated as an error.
//
// Note what is NOT validated here:
//  - Ownership. That is an ownership question, and answering it in a validator would make it a 400 that
//    distinguishes "no such row" from "somebody else's row". The handler answers it as a flat 404.
//  - The market cap and the planting-date range. Both are well-formed requests the product refuses, so
//    they are 422 wire codes from the handler (too_many_markets / invalid_planted_date), not 400s. The
//    date range in particular needs a clock, which belongs in a handler and not in a validator.
public class UpdateWatchlistEntryCommandValidator : AbstractValidator<UpdateWatchlistEntryCommand>
{
    public UpdateWatchlistEntryCommandValidator(IPortfolioReadStore store)
    {
        RuleFor(x => x.UserId)
            .NotEqual(Guid.Empty).WithMessage("The acting user could not be identified.");

        RuleFor(x => x.CropId)
            .NotEqual(Guid.Empty).WithMessage("cropId is required.");

        RuleForEach(x => x.MarketIds!)
            .NotEqual(Guid.Empty).WithMessage("marketIds must not contain an empty GUID.")
            .When(x => x.MarketIds is not null);

        RuleForEach(x => x.MarketIds!)
            .MustAsync(async (id, ct) => await store.GetMarketAsync(id, ct) is not null)
            .When(x => x.MarketIds is not null && x.MarketIds.All(id => id != Guid.Empty))
            .WithMessage("marketIds contains an id that does not match an existing market.");
    }
}
