using AgriForecast.Application.common;
using AgriForecast.Application.Requests.NewsEvents.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.NewsEvents.Commands.Create;

public class NewsEventCreateCommand : IRequest<Result<bool>>
{
    public NewsEvent_CreateDto NewsEventCreateDto { get; set; }

    /// <summary>Set server-side from the acting admin's JWT sub claim; any value in the request body is overwritten.</summary>
    public Guid ActingUserId { get; set; }
}
