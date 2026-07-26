using AgriForecast.Domain.Constants;
using FluentValidation;

namespace AgriForecast.Application.Requests.Users.Commands.UpdateRole;

/// <summary>
/// First-line, fail-closed guard on the role whitelist and identifiers. The handler re-checks the
/// whitelist as defence in depth, so the rule holds even if the pipeline validator is bypassed.
/// </summary>
public class UpdateUserRoleCommandValidator : AbstractValidator<UpdateUserRoleCommand>
{
    public UpdateUserRoleCommandValidator()
    {
        RuleFor(x => x.TargetUserId)
            .NotEmpty().WithMessage("Target user id is required.");

        RuleFor(x => x.ActingUserId)
            .NotEmpty().WithMessage("Acting user id is required.");

        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required.")
            // Case-sensitive exact match against the closed set, so a role can only ever be canonical.
            .Must(UserRoles.IsAssignable)
            .WithMessage($"Role must be one of: {string.Join(", ", UserRoles.Assignable)}.");
    }
}
