using AgriForecast.Application.common;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.PolicyFlag.Quaries.GetAll;

public class PolicyFlagGetAllQuery : IRequest<Result<List<PolicyFlag_GetDto>>>
{
    // When supplied, returns only flags active on that date; otherwise all flags ordered by EffectiveFrom.
    public DateTime? AsOfDate { get; set; }
}
