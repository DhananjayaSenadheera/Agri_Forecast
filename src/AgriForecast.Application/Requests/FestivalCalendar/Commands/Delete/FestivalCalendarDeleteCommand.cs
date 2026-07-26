using AgriForecast.Application.common;
using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.FestivalCalendar.Commands.Delete;

public class FestivalCalendarDeleteCommand : IRequest<Result<FestivalCalendar_MutationResultDto>>
{
    /// <summary>
    /// Admin-only deletion of a festival-calendar entry. actingUserId is stamped by the controller from
    /// the JWT sub claim (never the body or route) and is required so a new call site cannot forget it.
    /// </summary>
    public FestivalCalendarDeleteCommand(Guid id, Guid actingUserId)
    {
        Id = id;
        ActingUserId = actingUserId;
    }

    public Guid Id { get; set; }

    /// <summary>The acting admin (JWT <c>sub</c>), recorded on the audit row.</summary>
    public Guid ActingUserId { get; }
}
