using AgriForecast.Application.Requests.Market.Commands.Create;
using FluentValidation;

namespace AgriForecast.Application.Requests.Market.Validators;

// Name and District are required; MarketType must be a defined enum value, guarding an out-of-range int
// bound from the request body.
public class MarketCreateValidator : AbstractValidator<MarketCreateCommand>
{
    public MarketCreateValidator()
    {
        RuleFor(c => c.CreateDto).NotNull().WithMessage("Market details are required.");

        RuleFor(c => c.CreateDto.Name)
            .NotEmpty().WithMessage("Market name is required.")
            .MaximumLength(200).WithMessage("Market name must not exceed 200 characters.");

        RuleFor(c => c.CreateDto.District)
            .NotEmpty().WithMessage("District is required.")
            .MaximumLength(100).WithMessage("District must not exceed 100 characters.");

        RuleFor(c => c.CreateDto.MarketType)
            .IsInEnum().WithMessage("Market type is not valid.");
    }
}
