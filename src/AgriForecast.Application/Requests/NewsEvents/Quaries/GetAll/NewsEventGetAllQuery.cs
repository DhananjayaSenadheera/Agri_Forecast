using AgriForecast.Application.common;
using AgriForecast.Application.Requests.NewsEvents.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.NewsEvents.Quaries.GetAll;

// All captured news events, newest knowledge date first. The admin News page renders a
// reverse-chronological list.
public class NewsEventGetAllQuery : IRequest<Result<List<NewsEvent_GetDto>>>
{
}
