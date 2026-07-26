using AgriForecast.Application.common;
using AgriForecast.Application.Requests.NewsEvents.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.NewsEvents.Commands.Update;

// Returns the affected id. No training-data warning: NewsEvents are capture-and-storage only and are not
// ML inputs yet.
public class NewsEventUpdateCommand : IRequest<Result<Guid>>
{
    public NewsEvent_UpdateDto NewsEventUpdateDto { get; set; }

    /// <summary>Set server-side from the acting admin's JWT sub claim; any value in the request body is overwritten.</summary>
    public Guid ActingUserId { get; set; }
}
