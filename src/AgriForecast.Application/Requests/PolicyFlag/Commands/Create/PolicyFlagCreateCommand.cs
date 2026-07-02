using AgriForecast.Application.common;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.PolicyFlag.Commands.Create;

public class PolicyFlagCreateCommand : IRequest<Result<bool>>
{
    public PolicyFlag_CreateDto PolicyFlagCreateDto { get; set; }
}
