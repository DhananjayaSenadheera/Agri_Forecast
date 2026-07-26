using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.Users.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Users.Commands.Create;

/// <summary>
/// Creates a user account on behalf of an admin. Fail-closed guards: the role must be assignable (a
/// re-check of the validator), the username must not be taken, and the email must not be registered.
/// The uniqueness wording matches RegisterCommandHandler so an admin sees the same constraint a
/// self-registering farmer would hit.
/// <para>No token is issued: returning an AuthResponseDto here would hand the acting admin a cookie for
/// the account they just created. Only the AdminUserDto projection is returned.</para>
/// </summary>
public class CreateUserCommandHandler : IRequestHandler<CreateUserCommand, Result<AdminUserDto>>
{
    private readonly IUserRepository _userRepository;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IUserActivityAudit _activityAudit;
    private readonly ILogger<CreateUserCommandHandler> _logger;

    public CreateUserCommandHandler(
        IUserRepository userRepository,
        IUnitofWorkRepository unitofWorkRepository,
        IPasswordHasher passwordHasher,
        IUserActivityAudit activityAudit,
        ILogger<CreateUserCommandHandler> logger)
    {
        _userRepository = userRepository;
        _unitofWorkRepository = unitofWorkRepository;
        _passwordHasher = passwordHasher;
        _activityAudit = activityAudit;
        _logger = logger;
    }

    public async Task<Result<AdminUserDto>> Handle(CreateUserCommand request, CancellationToken cancellationToken)
    {
        if (!UserRoles.IsAssignable(request.Role))
            return Result<AdminUserDto>.Failure("Invalid role.");

        var existingByName = await _userRepository.GetByUsernameAsync(request.Username);
        if (existingByName is not null)
            return Result<AdminUserDto>.Failure("Username is already taken.");

        var existingByEmail = await _userRepository.GetByEmailAsync(request.Email);
        if (existingByEmail is not null)
            return Result<AdminUserDto>.Failure("Email is already registered.");

        var now = DateTime.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = request.Username,
            Email = request.Email,
            PasswordHash = _passwordHasher.Hash(request.Password),
            Role = request.Role,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _userRepository.AddAsync(user);
        await _unitofWorkRepository.CommitAsync();

        // Log identifiers and the role only — never the password, email, or request body.
        _logger.LogInformation(
            "Admin {ActingUserId} created user {NewUserId} with role {Role}.",
            request.ActingUserId, user.Id, user.Role);

        await _activityAudit.RecordUserCreatedByAdminAsync(request.ActingUserId, user.Id, cancellationToken);

        return Result<AdminUserDto>.Success(user.ToAdminDto());
    }
}
