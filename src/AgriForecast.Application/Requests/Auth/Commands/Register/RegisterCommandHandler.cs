using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Auth.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Auth.Commands.Register;

public class RegisterCommandHandler : IRequestHandler<RegisterCommand, Result<AuthResponseDto>>
{
    private readonly IUserRepository _userRepository;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IJwtTokenGenerator _jwtTokenGenerator;
    private readonly IUserActivityAudit _activityAudit;
    private readonly ILogger<RegisterCommandHandler> _logger;

    public RegisterCommandHandler(
        IUserRepository userRepository,
        IUnitofWorkRepository unitofWorkRepository,
        IPasswordHasher passwordHasher,
        IJwtTokenGenerator jwtTokenGenerator,
        IUserActivityAudit activityAudit,
        ILogger<RegisterCommandHandler> logger)
    {
        _userRepository = userRepository;
        _unitofWorkRepository = unitofWorkRepository;
        _passwordHasher = passwordHasher;
        _jwtTokenGenerator = jwtTokenGenerator;
        _activityAudit = activityAudit;
        _logger = logger;
    }

    public async Task<Result<AuthResponseDto>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var dto = request.RegisterDto;
        if (dto is null)
            return Result<AuthResponseDto>.Failure("Registration details are required.");

        var existingByName = await _userRepository.GetByUsernameAsync(dto.Username);
        if (existingByName is not null)
            return Result<AuthResponseDto>.Failure("Username is already taken.");

        var existingByEmail = await _userRepository.GetByEmailAsync(dto.Email);
        if (existingByEmail is not null)
            return Result<AuthResponseDto>.Failure("Email is already registered.");

        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = dto.Username,
            Email = dto.Email,
            PasswordHash = _passwordHasher.Hash(dto.Password),
            Role = "Farmer",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        await _userRepository.AddAsync(user);
        await _unitofWorkRepository.CommitAsync();
        _logger.LogInformation("New user registered. Username: {Username}", user.Username);
        await _activityAudit.RecordUserRegisteredAsync(user.Id, cancellationToken);

        var (token, expiresAt) = _jwtTokenGenerator.Generate(user);
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
