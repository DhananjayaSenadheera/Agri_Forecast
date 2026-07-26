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
/// Changes a user's role. Fail-closed guards: the new role must be assignable (a re-check of the
/// validator), the target must exist, and demoting the last remaining admin is refused (the Admin count is
/// read in the same request scope as the write). A no-op role change succeeds without a write.
/// <para>On an actual change all of the user's refresh-token families are revoked, so they must
/// re-authenticate to obtain a token carrying the new role. An already-issued access token keeps the old
/// role until it expires.</para>
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

        // The role actually changed, so revoke the refresh families and force a re-authentication.
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
