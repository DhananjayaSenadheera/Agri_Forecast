using AgriForecast.Domain.Constants;
using FluentValidation;

namespace AgriForecast.Application.Requests.Users.Commands.Create;

/// <summary>
/// First-line, fail-closed guard for admin-created accounts. The username/email/password rules are
/// kept IDENTICAL to <c>RegisterCommandValidator</c> on purpose — an account provisioned by an admin
/// must satisfy exactly the same constraints as a self-registered one, so the two paths can never
/// drift into producing differently-shaped users. The role whitelist is re-checked in the handler
/// (defence in depth), as in the update-role command.
/// </summary>
public class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator()
    {
        RuleFor(x => x.Username)
            .NotEmpty().WithMessage("Username is required.")
            .MaximumLength(50).WithMessage("Username cannot exceed 50 characters.");

        RuleFor(x => x.Email)
            .NotEmpty().WithMessage("Email is required.")
            .EmailAddress().WithMessage("A valid email is required.")
            .MaximumLength(256).WithMessage("Email cannot exceed 256 characters.");

        RuleFor(x => x.Password)
            .NotEmpty().WithMessage("Password is required.")
            .MinimumLength(8).WithMessage("Password must be at least 8 characters.")
            .MaximumLength(128).WithMessage("Password cannot exceed 128 characters.");

        RuleFor(x => x.Role)
            .NotEmpty().WithMessage("Role is required.")
            // Case-sensitive exact match against the closed set — "admin"/"ADMIN"/anything else is
            // rejected so a role can only ever be one of the canonical values.
            .Must(UserRoles.IsAssignable)
            .WithMessage($"Role must be one of: {string.Join(", ", UserRoles.Assignable)}.");

        RuleFor(x => x.ActingUserId)
            .NotEmpty().WithMessage("Acting user id is required.");
    }
}
