using AgriForecast.Application.common;
using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.FestivalCalendar.Quaries.GetAll;

// All festival calendar entries, ordered by Date. The admin Festivals page groups them by year.
public class FestivalCalendarGetAllQuery : IRequest<Result<List<FestivalCalendar_GetDto>>>
{
}
