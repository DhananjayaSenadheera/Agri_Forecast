using AgriForecast.Application.common;
using AgriForecast.Domain.Interfaces;
using AutoMapper;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.PolicyFlag.Commands.Create;

public class PolicyFlagCreateCommandHandler : IRequestHandler<PolicyFlagCreateCommand, Result<bool>>
{
    private readonly IPolicyFlagRepository _policyFlagRepository;
    private readonly IMapper _mapper;
    private readonly ILogger<PolicyFlagCreateCommandHandler> _logger;
    private readonly IUnitofWorkRepository _unitofWorkRepository;

    public PolicyFlagCreateCommandHandler(
        IPolicyFlagRepository policyFlagRepository,
        IMapper mapper,
        ILogger<PolicyFlagCreateCommandHandler> logger,
        IUnitofWorkRepository unitofWorkRepository)
    {
        _policyFlagRepository = policyFlagRepository;
        _mapper = mapper;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
    }

    public async Task<Result<bool>> Handle(PolicyFlagCreateCommand request, CancellationToken cancellationToken)
    {
        var dto = request.PolicyFlagCreateDto;
        if (dto is null)
        {
            _logger.LogInformation("Policy flag details are required.");
            return Result<bool>.Failure("Policy flag details are required.");
        }

        var policyFlag = _mapper.Map<Domain.Entities.PolicyFlag>(dto);
        await _policyFlagRepository.AddAsync(policyFlag);
        await _unitofWorkRepository.CommitAsync();
        _logger.LogInformation("Policy flag created. Title: {Title}, EffectiveFrom: {EffectiveFrom:yyyy-MM-dd}",
            policyFlag.Title, policyFlag.EffectiveFrom);
        return Result<bool>.Success(true);
    }
}
