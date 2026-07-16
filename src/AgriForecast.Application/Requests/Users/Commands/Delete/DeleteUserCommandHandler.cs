using AgriForecast.Application.common;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Users.Commands.Delete;

/// <summary>
/// Deletes a user. Guards (all fail-closed, all tested):
///  - you cannot delete your own account (acting id == target id);
///  - the target must exist;
///  - the LAST remaining admin cannot be deleted (Admin count checked in the same request scope as
///    the write).
/// NOTE on token lifetime: refresh tokens are stateless (no server-side store), so this delete
/// removes the row but cannot revoke an ALREADY-ISSUED access token — it stays valid until it
/// expires (&lt;= AccessTokenMinutes). The deleted user's next /api/auth/refresh fails closed
/// (GetByIdAsync miss -> 401). True immediate revocation is the separate refresh-revocation backlog
/// item; do not build it here.
/// </summary>
public class DeleteUserCommandHandler : IRequestHandler<DeleteUserCommand, Result<bool>>
{
    private readonly IUserRepository _userRepository;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly ILogger<DeleteUserCommandHandler> _logger;

    public DeleteUserCommandHandler(
        IUserRepository userRepository,
        IUnitofWorkRepository unitofWorkRepository,
        ILogger<DeleteUserCommandHandler> logger)
    {
        _userRepository = userRepository;
        _unitofWorkRepository = unitofWorkRepository;
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

        await _userRepository.DeleteAsync(target);
        await _unitofWorkRepository.CommitAsync();

        _logger.LogInformation(
            "Admin {ActingUserId} deleted user {TargetUserId}.",
            request.ActingUserId, target.Id);

        return Result<bool>.Success(true);
    }
}
