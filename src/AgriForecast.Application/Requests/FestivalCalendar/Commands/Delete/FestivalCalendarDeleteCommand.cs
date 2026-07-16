using AgriForecast.Application.common;
using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.FestivalCalendar.Commands.Delete;

public class FestivalCalendarDeleteCommand : IRequest<Result<FestivalCalendar_MutationResultDto>>
{
    public FestivalCalendarDeleteCommand(Guid id)
    {
        Id = id;
    }

    public Guid Id { get; set; }
}
