using AgriForecast.Application.common;
using AgriForecast.Application.Requests.NewsEvents.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.NewsEvents.Commands.Update;

// Returns the affected id on success. No mutation-result DTO / training-data warning: NewsEvents
// are capture-and-storage only and not yet ML inputs (deliberate divergence from API-10/13).
public class NewsEventUpdateCommand : IRequest<Result<Guid>>
{
    public NewsEvent_UpdateDto NewsEventUpdateDto { get; set; }
}
