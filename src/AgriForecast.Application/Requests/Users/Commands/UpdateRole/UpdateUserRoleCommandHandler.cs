using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.Users.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Users.Commands.UpdateRole;

/// <summary>
/// Changes a user's role. Guards (all fail-closed, all tested):
///  - the new role must be an assignable role (defence-in-depth re-check of the validator);
///  - the target user must exist;
///  - demoting the LAST remaining admin is refused (Admin count checked in the same request scope
///    as the write, so a normal sequential admin flow can never race the count to zero).
/// A no-op (role already equals the requested role) succeeds idempotently without a write.
/// REFRESH REVOCATION: on an ACTUAL role change all of the user's refresh-token families are revoked
/// so their next /api/auth/refresh is rejected and they must re-authenticate to obtain a token
/// carrying the new role. (An already-issued access token still carries the old role until it
/// expires &lt;= AccessTokenMinutes — the documented residual window.)
/// </summary>
public class UpdateUserRoleCommandHandler : IRequestHandler<UpdateUserRoleCommand, Result<AdminUserDto>>
{
    private readonly IUserRepository _userRepository;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IUserActivityAudit _activityAudit;
    private readonly ILogger<UpdateUserRoleCommandHandler> _logger;

    public UpdateUserRoleCommandHandler(
        IUserRepository userRepository,
        IUnitofWorkRepository unitofWorkRepository,
        IRefreshTokenService refreshTokenService,
        IUserActivityAudit activityAudit,
        ILogger<UpdateUserRoleCommandHandler> logger)
    {
        _userRepository = userRepository;
        _unitofWorkRepository = unitofWorkRepository;
        _refreshTokenService = refreshTokenService;
        _activityAudit = activityAudit;
        _logger = logger;
    }

    public async Task<Result<AdminUserDto>> Handle(UpdateUserRoleCommand request, CancellationToken cancellationToken)
    {
        if (!UserRoles.IsAssignable(request.Role))
            return Result<AdminUserDto>.Failure("Invalid role.");

        var target = await _userRepository.GetByIdAsync(request.TargetUserId);
        if (target is null)
            return Result<AdminUserDto>.Failure("User not found.");

        // Idempotent no-op: nothing to change.
        if (string.Equals(target.Role, request.Role, StringComparison.Ordinal))
            return Result<AdminUserDto>.Success(target.ToAdminDto());

        // Last-admin guard: refuse to demote the only remaining admin.
        var isDemotingAnAdmin = string.Equals(target.Role, UserRoles.Admin, StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(request.Role, UserRoles.Admin, StringComparison.Ordinal);
        if (isDemotingAnAdmin)
        {
            var adminCount = await _userRepository.CountByRoleAsync(UserRoles.Admin);
            if (adminCount <= 1)
                return Result<AdminUserDto>.Failure("Cannot demote the last remaining admin.");
        }

        target.Role = request.Role;
        target.UpdatedAt = DateTime.UtcNow;
        await _userRepository.UpdateAsync(target);
        await _unitofWorkRepository.CommitAsync();

        // Role actually changed → revoke the user's refresh families so their next refresh is
        // rejected and they re-authenticate into a token that reflects the new role.
        await _refreshTokenService.RevokeAllForUserAsync(target.Id, cancellationToken);

        // Log identifiers only — never request bodies, emails, or secrets.
        _logger.LogInformation(
            "Admin {ActingUserId} changed role of user {TargetUserId} to {Role}.",
            request.ActingUserId, target.Id, request.Role);
        await _activityAudit.RecordRoleChangedAsync(
            request.ActingUserId, target.Id, request.Role, cancellationToken);

        return Result<AdminUserDto>.Success(target.ToAdminDto());
    }
}
