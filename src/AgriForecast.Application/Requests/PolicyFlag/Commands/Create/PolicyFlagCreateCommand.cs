using AgriForecast.Application.common;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.PolicyFlag.Commands.Create;

public class PolicyFlagCreateCommand : IRequest<Result<bool>>
{
    public PolicyFlag_CreateDto PolicyFlagCreateDto { get; set; }

    /// <summary>
    /// The acting admin, stamped by the controller from the JWT <c>sub</c> claim. Any value supplied
    /// in the request body is OVERWRITTEN — it exists on the command only so the audit trail can name
    /// who made the change; it can never be forged by a caller.
    /// </summary>
    public Guid ActingUserId { get; set; }
}
