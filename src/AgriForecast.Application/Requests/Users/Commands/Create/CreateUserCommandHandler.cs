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
/// Creates a user account on behalf of an admin. Guards (all fail-closed, all tested):
///  - the requested role must be an assignable role (defence-in-depth re-check of the validator);
///  - the username must not already be taken;
///  - the email must not already be registered.
/// The uniqueness checks and their wording are kept identical to <c>RegisterCommandHandler</c> so an
/// admin sees exactly the constraint a self-registering farmer would hit.
/// <para>
/// NO TOKEN IS ISSUED. Self-registration returns an <c>AuthResponseDto</c> and the auth controller
/// turns that into a refresh cookie for the caller; doing the same here would hand the ACTING ADMIN
/// a cookie belonging to the account they just created. This handler returns only the
/// <see cref="AdminUserDto"/> projection (no hash, no token), so the admin's own session is untouched
/// and the new user signs in normally with the password they were given.
/// </para>
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
