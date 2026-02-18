using AgriForecast.Application.Requests.EcconomicCenter.Commands.Delete;
using FluentValidation;

namespace AgriForecast.Application.Requests.EcconomicCenter.Validators;

public class EcoDeleteValidator : AbstractValidator<EcoDeleteCommand>
{
    public EcoDeleteValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty().WithMessage("Id is required.")
            .Must(id => Guid.TryParse(id.ToString(), out _)).WithMessage("Invalid Id format.");
        
    }
}