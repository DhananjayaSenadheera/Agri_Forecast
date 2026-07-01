using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.PolicyFlag.Quaries.GetAll;

public class PolicyFlagGetAllQueryHandler : IRequestHandler<PolicyFlagGetAllQuery, Result<List<PolicyFlag_GetDto>>>
{
    private readonly IPolicyFlagRepository _policyFlagRepository;
    private readonly ILogger<PolicyFlagGetAllQueryHandler> _logger;

    public PolicyFlagGetAllQueryHandler(
        IPolicyFlagRepository policyFlagRepository,
        ILogger<PolicyFlagGetAllQueryHandler> logger)
    {
        _policyFlagRepository = policyFlagRepository;
        _logger = logger;
    }

    public async Task<Result<List<PolicyFlag_GetDto>>> Handle(PolicyFlagGetAllQuery request, CancellationToken cancellationToken)
    {
        var flags = request.AsOfDate.HasValue
            ? await _policyFlagRepository.GetActiveAsOfAsync(request.AsOfDate.Value)
            : await _policyFlagRepository.GetAllAsync();

        if (flags == null || !flags.Any())
        {
            _logger.LogInformation("No policy flags found.");
            return Result<List<PolicyFlag_GetDto>>.Failure("No policy flags found.");
        }

        var dtos = flags.ToGetDtoList();
        _logger.LogInformation("{Count} policy flags retrieved successfully.", dtos.Count);
        return Result<List<PolicyFlag_GetDto>>.Success(dtos);
    }
}
