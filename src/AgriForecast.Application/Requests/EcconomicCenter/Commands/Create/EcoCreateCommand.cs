using AgriForecast.Application.common;
using AgriForecast.Application.Requests.EcconomicCenter.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.EcconomicCenter.Commands.Create;

public class EcoCreateCommand : IRequest<Result<bool>>
{
    public Eco_CreateDto EcoCreateDto { get; set; }
}