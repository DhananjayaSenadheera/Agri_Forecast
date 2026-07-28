using AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;
using AgriForecast.Application.Services;
using FluentValidation;

namespace AgriForecast.Application.Requests.Portfolio.Validators;

// POST /api/portfolio/watchlist. Every id must resolve to a real row so a bad id is a structured 400
// rather than an opaque FK violation from the database.
//
// NOTE (deliberate deviation from the PR spec): the spec asked for "crop exists + ACTIVE". Crops has no
// IsActive column — activation lives on CommodityAliases (the per-source mapping) and on Markets, never on
// the crop itself — so existence is the only check that can honestly be made here. Nothing was invented to
// stand in for it.
//
// WHAT IS NOT HERE: the caps. "You already watch 10 crops" and "that is a 4th market" are not malformed
// requests, they are well-formed ones the product refuses — the handler answers them as 422 wire codes so
// the UI can tell the farmer which limit they hit. A 400 here would flatten both into "bad request".
//
// The existence checks live in their OWN RuleFor chains: a trailing .When() applies to every validator in
// its chain, so folding them in with the NotEqual guards would switch those guards off in exactly the case
// they exist to catch.
public class AddWatchlistCropCommandValidator : AbstractValidator<AddWatchlistCropCommand>
{
    public AddWatchlistCropCommandValidator(IPortfolioReadStore store)
    {
        // Defence in depth: the controller always stamps this from the JWT and refuses the request when
        // the subject claim is missing, so an empty id here would mean the wiring changed.
        RuleFor(x => x.UserId)
            .NotEqual(Guid.Empty).WithMessage("The acting user could not be identified.");

        RuleFor(x => x.CropId)
            .NotEqual(Guid.Empty).WithMessage("cropId is required.");

        RuleFor(x => x.CropId)
            .MustAsync((id, ct) => store.CropExistsAsync(id, ct))
            .When(x => x.CropId != Guid.Empty)
            .WithMessage("cropId does not match an existing crop.");

        // Only checked when markets were actually supplied — omitted means "add the crop with no markets",
        // which is a legitimate state and not a value to validate.
        RuleForEach(x => x.MarketIds!)
            .NotEqual(Guid.Empty).WithMessage("marketIds must not contain an empty GUID.")
            .When(x => x.MarketIds is not null);

        RuleForEach(x => x.MarketIds!)
            .MustAsync(async (id, ct) => await store.GetMarketAsync(id, ct) is not null)
            .When(x => x.MarketIds is not null && x.MarketIds.All(id => id != Guid.Empty))
            .WithMessage("marketIds contains an id that does not match an existing market.");
    }
}
