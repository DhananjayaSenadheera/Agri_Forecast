using AgriForecast.Application.common;
using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.FestivalCalendar.Commands.Create;

public class FestivalCalendarCreateCommand : IRequest<Result<bool>>
{
    public FestivalCalendar_CreateDto FestivalCalendarCreateDto { get; set; }

    /// <summary>
    /// The acting admin, stamped by the controller from the JWT <c>sub</c> claim. Any value supplied
    /// in the request body is OVERWRITTEN — it exists on the command only so the audit trail can name
    /// who made the change; it can never be forged by a caller.
    /// </summary>
    public Guid ActingUserId { get; set; }
}
