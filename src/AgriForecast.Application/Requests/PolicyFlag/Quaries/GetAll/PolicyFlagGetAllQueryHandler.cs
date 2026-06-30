using AgriForecast.Application.common;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using AgriForecast.Domain.Interfaces;
using AutoMapper;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.PolicyFlag.Quaries.GetAll;

public class PolicyFlagGetAllQueryHandler : IRequestHandler<PolicyFlagGetAllQuery, Result<List<PolicyFlag_GetDto>>>
{
    private readonly IPolicyFlagRepository _policyFlagRepository;
    private readonly IMapper _mapper;
    private readonly ILogger<PolicyFlagGetAllQueryHandler> _logger;

    public PolicyFlagGetAllQueryHandler(
        IPolicyFlagRepository policyFlagRepository,
        IMapper mapper,
        ILogger<PolicyFlagGetAllQueryHandler> logger)
    {
        _policyFlagRepository = policyFlagRepository;
        _mapper = mapper;
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

        var dtos = _mapper.Map<List<PolicyFlag_GetDto>>(flags);
        _logger.LogInformation("{Count} policy flags retrieved successfully.", dtos.Count);
        return Result<List<PolicyFlag_GetDto>>.Success(dtos);
    }
}
