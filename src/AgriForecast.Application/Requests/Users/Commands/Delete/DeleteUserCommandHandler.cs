using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Users.Commands.Delete;

/// <summary>
/// Deletes a user. Fail-closed guards: you cannot delete your own account, the target must exist, and the
/// last remaining admin cannot be deleted (the Admin count is read in the same request scope as the write).
/// <para>Before deleting, all of the target's refresh-token families are revoked so no outstanding refresh
/// token survives. An already-issued access token stays valid until it expires — that short window is the
/// documented residual limit.</para>
/// </summary>
public class DeleteUserCommandHandler : IRequestHandler<DeleteUserCommand, Result<bool>>
{
    private readonly IUserRepository _userRepository;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IRefreshTokenService _refreshTokenService;
    private readonly IUserActivityAudit _activityAudit;
    private readonly ILogger<DeleteUserCommandHandler> _logger;

    public DeleteUserCommandHandler(
        IUserRepository userRepository,
        IUnitofWorkRepository unitofWorkRepository,
        IRefreshTokenService refreshTokenService,
        IUserActivityAudit activityAudit,
        ILogger<DeleteUserCommandHandler> logger)
    {
        _userRepository = userRepository;
        _unitofWorkRepository = unitofWorkRepository;
        _refreshTokenService = refreshTokenService;
        _activityAudit = activityAudit;
        _logger = logger;
    }

    public async Task<Result<bool>> Handle(DeleteUserCommand request, CancellationToken cancellationToken)
    {
        // Self-delete guard first — cheap and independent of the target's existence.
        if (request.TargetUserId == request.ActingUserId)
            return Result<bool>.Failure("You cannot delete your own account.");

        var target = await _userRepository.GetByIdAsync(request.TargetUserId);
        if (target is null)
            return Result<bool>.Failure("User not found.");

        // Last-admin guard: refuse to delete the only remaining admin.
        if (string.Equals(target.Role, UserRoles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            var adminCount = await _userRepository.CountByRoleAsync(UserRoles.Admin);
            if (adminCount <= 1)
                return Result<bool>.Failure("Cannot delete the last remaining admin.");
        }

        // Revoke every refresh-token family before the delete so no outstanding refresh token survives.
        await _refreshTokenService.RevokeAllForUserAsync(target.Id, cancellationToken);

        await _userRepository.DeleteAsync(target);
        await _unitofWorkRepository.CommitAsync();

        _logger.LogInformation(
            "Admin {ActingUserId} deleted user {TargetUserId}.",
            request.ActingUserId, target.Id);
        await _activityAudit.RecordUserDeletedAsync(request.ActingUserId, target.Id, cancellationToken);

        return Result<bool>.Success(true);
    }
}
