using AgriForecast.Application.Requests.EcconomicCenter.Quaries.GetOneById;
using FluentValidation;

namespace AgriForecast.Application.Requests.EcconomicCenter.Validators;

public class EcoGetByIdValidator : AbstractValidator<EcoGetByIdQuery>
{
    public EcoGetByIdValidator()
    {
        RuleFor(x => x.Guid)
            .NotEmpty().WithMessage("Id is required.")
            .Must(id => Guid.TryParse(id.ToString(), out _)).WithMessage("Invalid Id format.");
    }
    
}