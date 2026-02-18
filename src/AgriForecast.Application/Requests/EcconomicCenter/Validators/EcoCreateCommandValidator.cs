using AgriForecast.Application.Requests.EcconomicCenter.Commands.Create;
using FluentValidation;

namespace AgriForecast.Application.Requests.EcconomicCenter.Validators;

public class EcoCreateCommandValidator : AbstractValidator<EcoCreateCommand>
{
    public EcoCreateCommandValidator()
    {
        RuleFor(x => x.EcoCreateDto.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(20).WithMessage("Name cannot exceed 20 characters.");
        
            RuleFor(x => x.EcoCreateDto.Location)
                .NotEmpty().WithMessage("Location is required.")
                .MaximumLength(100).WithMessage("Location cannot exceed 100 characters.");
            
    }
    
}