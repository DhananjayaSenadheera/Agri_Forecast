using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.EcconomicCenter.Commands.Delete;

public class EcoDeleteCommand : IRequest<Result<bool>>
{
    public EcoDeleteCommand(Guid id)
    {
        Id = id;
    }
    public Guid Id { get; set; }
}