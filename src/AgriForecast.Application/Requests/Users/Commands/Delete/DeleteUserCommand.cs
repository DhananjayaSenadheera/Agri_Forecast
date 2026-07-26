using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Users.Commands.Delete;

/// <summary>
/// Admin-only deletion of a user. TargetUserId comes from the route; ActingUserId is stamped by the
/// controller from the JWT sub claim. Fail-closed: you cannot delete yourself or the last admin.
/// </summary>
public class DeleteUserCommand : IRequest<Result<bool>>
{
    public DeleteUserCommand(Guid targetUserId, Guid actingUserId)
    {
        TargetUserId = targetUserId;
        ActingUserId = actingUserId;
    }

    public Guid TargetUserId { get; }
    public Guid ActingUserId { get; }
}
