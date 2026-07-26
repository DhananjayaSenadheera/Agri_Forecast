using AgriForecast.Application.common;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.PolicyFlag.Commands.Update;

public class PolicyFlagUpdateCommand : IRequest<Result<PolicyFlag_MutationResultDto>>
{
    public PolicyFlag_UpdateDto PolicyFlagUpdateDto { get; set; }

    /// <summary>Set server-side from the acting admin's JWT sub claim; any value in the request body is overwritten.</summary>
    public Guid ActingUserId { get; set; }
}
