using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.NewsEvents.Commands.Delete;

public class NewsEventDeleteCommand : IRequest<Result<Guid>>
{
    public NewsEventDeleteCommand(Guid id)
    {
        Id = id;
    }

    public Guid Id { get; set; }
}
