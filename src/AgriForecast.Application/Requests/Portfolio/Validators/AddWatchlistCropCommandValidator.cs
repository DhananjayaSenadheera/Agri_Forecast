using AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;
using AgriForecast.Application.Services;
using FluentValidation;

namespace AgriForecast.Application.Requests.Portfolio.Validators;

// POST /api/portfolio/watchlist. Both ids must resolve to real rows so a bad id is a structured 400 rather
// than an opaque FK violation from the database.
//
// NOTE (deliberate deviation from the PR spec): the spec asked for "crop exists + ACTIVE". Crops has no
// IsActive column — activation lives on CommodityAliases (the per-source mapping) and on Markets, never on
// the crop itself — so existence is the only check that can honestly be made here. Nothing was invented to
// stand in for it.
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

        // Only checked when a market was actually supplied — omitted means "inherit the caller's current
        // home market", which is not a value to validate.
        //
        // OverridePropertyName because the rule is expressed on the nullable's .Value: without it the 400
        // body keys the error under "PreferredMarketId.Value", an implementation detail of this validator
        // rather than the name of anything the caller sent.
        RuleFor(x => x.PreferredMarketId!.Value)
            .NotEqual(Guid.Empty)
            .When(x => x.PreferredMarketId.HasValue)
            .OverridePropertyName(nameof(AddWatchlistCropCommand.PreferredMarketId))
            .WithMessage("preferredMarketId must not be an empty GUID.");

        RuleFor(x => x.PreferredMarketId!.Value)
            .MustAsync(async (id, ct) => await store.GetMarketAsync(id, ct) is not null)
            .When(x => x.PreferredMarketId.HasValue && x.PreferredMarketId.Value != Guid.Empty)
            .OverridePropertyName(nameof(AddWatchlistCropCommand.PreferredMarketId))
            .WithMessage("preferredMarketId does not match an existing market.");
    }
}
