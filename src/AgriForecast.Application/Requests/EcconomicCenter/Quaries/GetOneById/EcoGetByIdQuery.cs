using AgriForecast.Application.common;
using AgriForecast.Application.Requests.EcconomicCenter.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.EcconomicCenter.Quaries.GetOneById;

public class EcoGetByIdQuery : IRequest<Result<Eco_GetDto>>
{
    public EcoGetByIdQuery(Guid id)
    {
        Guid = id;
    }
    public Guid Guid { get; set; }
}