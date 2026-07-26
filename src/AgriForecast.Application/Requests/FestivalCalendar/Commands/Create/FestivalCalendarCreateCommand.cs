using AgriForecast.Application.common;
using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.FestivalCalendar.Commands.Create;

public class FestivalCalendarCreateCommand : IRequest<Result<bool>>
{
    public FestivalCalendar_CreateDto FestivalCalendarCreateDto { get; set; }

    /// <summary>Set server-side from the acting admin's JWT sub claim; any value in the request body is overwritten.</summary>
    public Guid ActingUserId { get; set; }
}
