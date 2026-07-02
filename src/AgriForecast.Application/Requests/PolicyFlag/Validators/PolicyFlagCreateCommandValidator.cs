using AgriForecast.Application.Requests.PolicyFlag.Commands.Create;
using FluentValidation;

namespace AgriForecast.Application.Requests.PolicyFlag.Validators;

public class PolicyFlagCreateCommandValidator : AbstractValidator<PolicyFlagCreateCommand>
{
    public PolicyFlagCreateCommandValidator()
    {
        RuleFor(x => x.PolicyFlagCreateDto).NotNull().WithMessage("Policy flag details are required.");

        RuleFor(x => x.PolicyFlagCreateDto.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(200).WithMessage("Title cannot exceed 200 characters.");

        RuleFor(x => x.PolicyFlagCreateDto.Description)
            .MaximumLength(2000).WithMessage("Description cannot exceed 2000 characters.");

        RuleFor(x => x.PolicyFlagCreateDto.PolicyType)
            .IsInEnum().WithMessage("PolicyType is invalid.");

        RuleFor(x => x.PolicyFlagCreateDto.Direction)
            .IsInEnum().WithMessage("Direction is invalid.");

        RuleFor(x => x.PolicyFlagCreateDto.EffectiveFrom)
            .NotEmpty().WithMessage("EffectiveFrom is required.");

        // EffectiveTo is optional (null = still in effect), but when present must not precede EffectiveFrom.
        RuleFor(x => x.PolicyFlagCreateDto.EffectiveTo)
            .GreaterThanOrEqualTo(x => x.PolicyFlagCreateDto.EffectiveFrom)
            .When(x => x.PolicyFlagCreateDto != null && x.PolicyFlagCreateDto.EffectiveTo.HasValue)
            .WithMessage("EffectiveTo must be on or after EffectiveFrom.");

        RuleFor(x => x.PolicyFlagCreateDto.Source)
            .MaximumLength(200).WithMessage("Source cannot exceed 200 characters.");

        RuleFor(x => x.PolicyFlagCreateDto.ReferenceUrl)
            .MaximumLength(500).WithMessage("ReferenceUrl cannot exceed 500 characters.");
    }
}
