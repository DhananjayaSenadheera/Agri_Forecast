using AgriForecast.Application.Requests.Auth.Commands.Login;
using FluentValidation;

namespace AgriForecast.Application.Requests.Auth.Validators;

public class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(x => x.LoginDto.Username)
            .NotEmpty().WithMessage("Username is required.");

        RuleFor(x => x.LoginDto.Password)
            .NotEmpty().WithMessage("Password is required.");
    }
}
