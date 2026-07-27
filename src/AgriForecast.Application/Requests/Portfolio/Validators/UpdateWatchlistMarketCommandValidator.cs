using AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistMarket;
using AgriForecast.Application.Services;
using FluentValidation;

namespace AgriForecast.Application.Requests.Portfolio.Validators;

// PUT /api/portfolio/watchlist/{cropId}.
//
// The market must exist WHEN SUPPLIED. A null preferredMarketId is valid and meaningful here — it clears
// the home market back to the national / economic-centre default — so it is not treated as a missing field.
//
// Note what is NOT validated here: whether the caller actually watches that crop. That is an OWNERSHIP
// question, and answering it in a validator would make it a 400 that distinguishes "no such row" from
// "somebody else's row". The handler answers it as a flat 404 instead.
public class UpdateWatchlistMarketCommandValidator : AbstractValidator<UpdateWatchlistMarketCommand>
{
    public UpdateWatchlistMarketCommandValidator(IPortfolioReadStore store)
    {
        RuleFor(x => x.UserId)
            .NotEqual(Guid.Empty).WithMessage("The acting user could not be identified.");

        RuleFor(x => x.CropId)
            .NotEqual(Guid.Empty).WithMessage("cropId is required.");

        RuleFor(x => x.PreferredMarketId!.Value)
            .NotEqual(Guid.Empty)
            .When(x => x.PreferredMarketId.HasValue)
            .OverridePropertyName(nameof(UpdateWatchlistMarketCommand.PreferredMarketId))
            .WithMessage("preferredMarketId must not be an empty GUID.");

        RuleFor(x => x.PreferredMarketId!.Value)
            .MustAsync(async (id, ct) => await store.GetMarketAsync(id, ct) is not null)
            .When(x => x.PreferredMarketId.HasValue && x.PreferredMarketId.Value != Guid.Empty)
            .OverridePropertyName(nameof(UpdateWatchlistMarketCommand.PreferredMarketId))
            .WithMessage("preferredMarketId does not match an existing market.");
    }
}
