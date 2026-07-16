using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Users.Commands.Delete;

/// <summary>
/// Admin-only deletion of a user. <see cref="TargetUserId"/> comes from the route;
/// <see cref="ActingUserId"/> is stamped by the controller from the JWT <c>sub</c> claim (never the
/// body/route). Fail-closed guards: you cannot delete yourself, and you cannot delete the last
/// remaining admin.
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
