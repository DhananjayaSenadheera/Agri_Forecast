using AgriForecast.Application.common;
using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.FestivalCalendar.Commands.Update;

public class FestivalCalendarUpdateCommand : IRequest<Result<FestivalCalendar_MutationResultDto>>
{
    public FestivalCalendar_UpdateDto FestivalCalendarUpdateDto { get; set; }
}
