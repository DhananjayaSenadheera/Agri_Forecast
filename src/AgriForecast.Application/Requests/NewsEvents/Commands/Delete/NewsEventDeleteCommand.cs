using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.NewsEvents.Commands.Delete;

public class NewsEventDeleteCommand : IRequest<Result<Guid>>
{
    /// <summary>
    /// Admin-only deletion of a news event. actingUserId is stamped by the controller from the JWT sub
    /// claim (never the body or route) and is required so a new call site cannot forget it.
    /// </summary>
    public NewsEventDeleteCommand(Guid id, Guid actingUserId)
    {
        Id = id;
        ActingUserId = actingUserId;
    }

    public Guid Id { get; set; }

    /// <summary>The acting admin (JWT <c>sub</c>), recorded on the audit row.</summary>
    public Guid ActingUserId { get; }
}
