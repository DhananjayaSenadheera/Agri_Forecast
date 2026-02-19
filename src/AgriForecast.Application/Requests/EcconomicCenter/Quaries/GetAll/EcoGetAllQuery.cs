using AgriForecast.Application.common;
using AgriForecast.Application.Requests.EcconomicCenter.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.EcconomicCenter.Quaries.GetAll;

public class EcoGetAllQuery: IRequest<Result<List<Eco_GetDto>>>
{
    
}