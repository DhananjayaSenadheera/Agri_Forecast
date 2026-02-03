using AgriForecast.Application.Requests.Crop.Commands.Create;
using FluentValidation;

namespace AgriForecast.Application.Requests.Crop.Validators;

public class CropCreateValidator : AbstractValidator<CropCreateCommand>
{
    public CropCreateValidator()
    {
        RuleFor(c => c.CreateDto.Name)
            .NotEmpty().WithMessage("Crop name is required.")
            .MaximumLength(100).WithMessage("Crop name must not exceed 15 characters.");
    }
}