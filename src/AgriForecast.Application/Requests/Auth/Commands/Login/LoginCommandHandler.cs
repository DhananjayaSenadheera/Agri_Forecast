using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Auth.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Auth.Commands.Login;

public class LoginCommandHandler : IRequestHandler<LoginCommand, Result<AuthResponseDto>>
{
    private readonly IUserRepository _userRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly ILogger<LoginCommandHandler> _logger;

    public LoginCommandHandler(
        IUserRepository userRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator,
        ILogger<LoginCommandHandler> logger)
    {
        _userRepository = userRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenGenerator = jwtTokenGenerator;
        _logger = logger;
    }

    public async Task<Result<AuthResponseDto>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var dto = request.LoginDto;
        if (dto is null)
            return Result<AuthResponseDto>.Failure("Login details are required.");

        var user = await _userRepository.GetByUsernameAsync(dto.Username);

        // Use the same generic message for unknown-user and bad-password to avoid leaking which usernames exist.
        if (user is null || !_passwordHasher.Verify(user.PasswordHash, dto.Password))
        {
            _logger.LogInformation("Failed login attempt for username: {Username}", dto.Username);
            return Result<AuthResponseDto>.Failure("Invalid username or password.");
        }

        var (token, expiresAt) = _jwtTokenGenerator.Generate(user);
        _logger.LogInformation("User logged in. Username: {Username}", user.Username);

        var response = new AuthResponseDto
        {
            AccessToken = token,
            ExpiresAtUtc = expiresAt,
            Username = user.Username,
            Email = user.Email,
            Role = user.Role
        };

        return Result<AuthResponseDto>.Success(response);
    }
}
