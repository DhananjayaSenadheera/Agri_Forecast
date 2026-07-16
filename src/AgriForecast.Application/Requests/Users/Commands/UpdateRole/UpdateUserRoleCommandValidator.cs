using AgriForecast.Domain.Constants;
using FluentValidation;

namespace AgriForecast.Application.Requests.Users.Commands.UpdateRole;

/// <summary>
/// First-line, fail-closed guard on the role whitelist and identifiers. The handler ALSO re-checks
/// the whitelist (defence in depth) so the rule holds even if the pipeline validator is ever bypassed.
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
            // Case-sensitive exact match against the closed set — "admin"/"ADMIN"/anything else is
            // rejected so a role can only ever be one of the canonical values.
            .Must(UserRoles.IsAssignable)
            .WithMessage($"Role must be one of: {string.Join(", ", UserRoles.Assignable)}.");
    }
}
