using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Market.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Market.Commands.Create;

public class MarketCreateCommand : IRequest<Result<bool>>
{
    public Market_CreateDto CreateDto { get; set; }

    /// <summary>Set server-side from the acting admin's JWT sub claim; any value in the request body is overwritten.</summary>
    public Guid ActingUserId { get; set; }
}
