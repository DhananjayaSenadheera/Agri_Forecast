using AgriForecast.Application.Requests.Portfolio.Commands.UpdateSale;
using FluentValidation;

namespace AgriForecast.Application.Requests.Portfolio.Validators;

// PUT /api/portfolio/sales/{id} — the STRUCTURAL guards only, for the same reasons set out in
// RecordSaleCommandValidator.
//
// OWNERSHIP IS NOT VALIDATED HERE. Answering "is this your sale?" in a validator would make it a 400 that
// distinguishes "no such sale" from "somebody else's sale"; the handler answers both as a flat 404. Same
// precedent as UpdateWatchlistEntryCommandValidator.
//
// There is deliberately no cropId rule, because there is deliberately no cropId: a sale's crop is immutable
// (wrong crop = delete and re-add), and the field's absence from the command is the enforcement.
public class UpdateSaleCommandValidator : AbstractValidator<UpdateSaleCommand>
{
    public UpdateSaleCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEqual(Guid.Empty).WithMessage("The acting user could not be identified.");

        RuleFor(x => x.SaleId)
            .NotEqual(Guid.Empty).WithMessage("id is required.");
    }
}
