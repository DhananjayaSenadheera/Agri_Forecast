using AgriForecast.Application.Requests.PolicyFlag.Commands.Update;
using FluentValidation;

namespace AgriForecast.Application.Requests.PolicyFlag.Validators;

// Mirrors PolicyFlagCreateCommandValidator plus two mutation rules: Id is required, and Source is
// required on edit (stricter than create).
public class PolicyFlagUpdateCommandValidator : AbstractValidator<PolicyFlagUpdateCommand>
{
    public PolicyFlagUpdateCommandValidator()
    {
        RuleFor(x => x.PolicyFlagUpdateDto).NotNull().WithMessage("Policy flag details are required.");

        RuleFor(x => x.PolicyFlagUpdateDto.Id)
            .NotEmpty().WithMessage("Id is required.");

        RuleFor(x => x.PolicyFlagUpdateDto.Title)
            .NotEmpty().WithMessage("Title is required.")
            .MaximumLength(200).WithMessage("Title cannot exceed 200 characters.");

        RuleFor(x => x.PolicyFlagUpdateDto.Description)
            .MaximumLength(2000).WithMessage("Description cannot exceed 2000 characters.");

        RuleFor(x => x.PolicyFlagUpdateDto.PolicyType)
            .IsInEnum().WithMessage("PolicyType is invalid.");

        RuleFor(x => x.PolicyFlagUpdateDto.Direction)
            .IsInEnum().WithMessage("Direction is invalid.");

        RuleFor(x => x.PolicyFlagUpdateDto.EffectiveFrom)
            .NotEmpty().WithMessage("EffectiveFrom is required.");

        // EffectiveTo is optional (null = still in effect), but when present must not precede EffectiveFrom.
        RuleFor(x => x.PolicyFlagUpdateDto.EffectiveTo)
            .GreaterThanOrEqualTo(x => x.PolicyFlagUpdateDto.EffectiveFrom)
            .When(x => x.PolicyFlagUpdateDto != null && x.PolicyFlagUpdateDto.EffectiveTo.HasValue)
            .WithMessage("EffectiveTo must be on or after EffectiveFrom.");

        // Source is mandatory on edit (unlike create): every mutation of training-relevant data is cited.
        RuleFor(x => x.PolicyFlagUpdateDto.Source)
            .NotEmpty().WithMessage("Source is required when editing a policy flag.")
            .MaximumLength(200).WithMessage("Source cannot exceed 200 characters.");

        RuleFor(x => x.PolicyFlagUpdateDto.ReferenceUrl)
            .MaximumLength(500).WithMessage("ReferenceUrl cannot exceed 500 characters.");
    }
}
