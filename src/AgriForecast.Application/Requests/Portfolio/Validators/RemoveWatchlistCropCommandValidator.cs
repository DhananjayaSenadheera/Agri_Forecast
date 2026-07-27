using AgriForecast.Application.Requests.Portfolio.Commands.RemoveWatchlistCrop;
using FluentValidation;

namespace AgriForecast.Application.Requests.Portfolio.Validators;

// DELETE /api/portfolio/watchlist/{cropId}. Shape only: a well-formed crop id the caller does not watch is
// not a validation failure, it is a 404 from the handler. Deliberately no existence check on the crop —
// that would turn "this crop was deleted from the catalogue" into a 400 the farmer cannot act on, and it
// would leak the difference between an unknown crop and an unwatched one.
public class RemoveWatchlistCropCommandValidator : AbstractValidator<RemoveWatchlistCropCommand>
{
    public RemoveWatchlistCropCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEqual(Guid.Empty).WithMessage("The acting user could not be identified.");

        RuleFor(x => x.CropId)
            .NotEqual(Guid.Empty).WithMessage("cropId is required.");
    }
}
