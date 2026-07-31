using AgriForecast.Application.Requests.Portfolio.Commands.RecordSale;
using FluentValidation;

namespace AgriForecast.Application.Requests.Portfolio.Validators;

// POST /api/portfolio/sales — the STRUCTURAL guards only.
//
// What is NOT here, and why: every content rule on this endpoint (price, ceiling, date, future date,
// quantity, note length, unknown crop, unknown market) is answered with a PINNED WIRE CODE the UI switches
// on to decide which field to highlight. A FluentValidation failure is prose in the { errors: [{ property,
// message }] } shape, which the UI would have to parse — so those rules live in the handler, exactly as the
// clear-reason contract does on PUT /api/portfolio/watchlist/{cropId}. Two of them need a clock or the
// database as well, neither of which belongs in a validator.
//
// What IS here is the pair of things that can only mean "the wiring is broken", not "the farmer mis-typed":
// an unstamped user (the controller always fills it from the JWT and 401s when it cannot) and a missing
// crop id, which is the one required id the route does not supply.
public class RecordSaleCommandValidator : AbstractValidator<RecordSaleCommand>
{
    public RecordSaleCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEqual(Guid.Empty).WithMessage("The acting user could not be identified.");

        RuleFor(x => x.CropId)
            .NotEqual(Guid.Empty).WithMessage("cropId is required.");
    }
}
