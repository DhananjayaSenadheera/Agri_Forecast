using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Auth.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Auth.Commands.Refresh;

public class RefreshCommandHandler : IRequestHandler<RefreshCommand, Result<AuthResponseDto>>
{
    private readonly IUserRepository _userRepository;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly ILogger<RefreshCommandHandler> _logger;

    public RefreshCommandHandler(
        IUserRepository userRepository,
        IJwtTokenGenerator jwtTokenGenerator,
        ILogger<RefreshCommandHandler> logger)
    {
        _userRepository = userRepository;
        _jwtTokenGenerator = jwtTokenGenerator;
        _logger = logger;
    }

    public async Task<Result<AuthResponseDto>> Handle(RefreshCommand request, CancellationToken cancellationToken)
    {
        // Same generic failure message for every invalid case (missing / malformed / expired /
        // wrong-type token, deleted user) so nothing leaks about why the refresh was rejected.
        const string invalidMessage = "Invalid or expired refresh token.";

        var subject = _jwtTokenGenerator.ValidateRefreshToken(request.RefreshToken);
        if (subject is null || !Guid.TryParse(subject, out var userId))
        {
            _logger.LogInformation("Refresh rejected: missing or invalid refresh token.");
            return Result<AuthResponseDto>.Failure(invalidMessage);
        }

        // Resolve by the IMMUTABLE user id embedded in the refresh token, never by username.
        var user = await _userRepository.GetByIdAsync(userId);
        if (user is null)
        {
            // Valid signature but the account no longer exists — treat as invalid.
            _logger.LogInformation("Refresh rejected: user no longer exists. UserId: {UserId}", userId);
            return Result<AuthResponseDto>.Failure(invalidMessage);
        }

        // Re-resolve username/email/role from the store (they may have changed since issuance).
        var (token, expiresAt) = _jwtTokenGenerator.Generate(user);
        _logger.LogInformation("Access token refreshed. UserId: {UserId}", user.Id);

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
