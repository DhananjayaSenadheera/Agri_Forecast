using AgriForecast.Application.common;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.PolicyFlag.Commands.Delete;

public class PolicyFlagDeleteCommand : IRequest<Result<PolicyFlag_MutationResultDto>>
{
    public PolicyFlagDeleteCommand(Guid id)
    {
        Id = id;
    }

    public Guid Id { get; set; }
}
