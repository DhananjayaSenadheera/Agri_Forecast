using AgriForecast.Application.Requests.EcconomicCenter.Commands.Update;
using FluentValidation;

namespace AgriForecast.Application.Requests.EcconomicCenter.Validators;

public class EcoUpdateValidator : AbstractValidator<EcoUpdateCommand>
{
    public EcoUpdateValidator()
    {
        RuleFor(x => x.EcoUpdateDto.Id).
            NotEmpty().WithMessage("Id is required")
            .Must(id => Guid.TryParse(id.ToString(), out _)).WithMessage("Invalid Id format.");
        
        RuleFor(x => x.EcoUpdateDto.Name)
            .NotEmpty().WithMessage("Name is required")
            .MaximumLength(20).WithMessage("Name must not exceed 20 characters");

        RuleFor(x => x.EcoUpdateDto.Location)
            .NotEmpty().WithMessage("Location is required")
            .MaximumLength(20).WithMessage("Location must not exceed 20 characters");

    }
    
}