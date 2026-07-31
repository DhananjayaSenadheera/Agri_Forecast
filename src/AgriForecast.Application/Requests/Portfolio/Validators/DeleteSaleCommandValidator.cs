using AgriForecast.Application.Requests.Portfolio.Commands.DeleteSale;
using FluentValidation;

namespace AgriForecast.Application.Requests.Portfolio.Validators;

// DELETE /api/portfolio/sales/{id}. Shape only: a well-formed id the caller does not own is not a
// validation failure, it is a 404 from the handler — and answering it any other way would leak which ids
// exist. Precedent: RemoveWatchlistCropCommandValidator.
public class DeleteSaleCommandValidator : AbstractValidator<DeleteSaleCommand>
{
    public DeleteSaleCommandValidator()
    {
        RuleFor(x => x.UserId)
            .NotEqual(Guid.Empty).WithMessage("The acting user could not be identified.");

        RuleFor(x => x.SaleId)
            .NotEqual(Guid.Empty).WithMessage("id is required.");
    }
}
