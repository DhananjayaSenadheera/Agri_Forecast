using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.PolicyFlag.Commands.Create;

public class PolicyFlagCreateCommandHandler : IRequestHandler<PolicyFlagCreateCommand, Result<bool>>
{
    private readonly IPolicyFlagRepository _policyFlagRepository;
    private readonly ILogger<PolicyFlagCreateCommandHandler> _logger;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IUserActivityAudit _activityAudit;

    public PolicyFlagCreateCommandHandler(
        IPolicyFlagRepository policyFlagRepository,
        ILogger<PolicyFlagCreateCommandHandler> logger,
        IUnitofWorkRepository unitofWorkRepository,
        IUserActivityAudit activityAudit)
    {
        _policyFlagRepository = policyFlagRepository;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _activityAudit = activityAudit;
    }

    public async Task<Result<bool>> Handle(PolicyFlagCreateCommand request, CancellationToken cancellationToken)
    {
        var dto = request.PolicyFlagCreateDto;
        if (dto is null)
        {
            _logger.LogInformation("Policy flag details are required.");
            return Result<bool>.Failure("Policy flag details are required.");
        }

        var policyFlag = dto.ToEntity();
        await _policyFlagRepository.AddAsync(policyFlag);
        await _unitofWorkRepository.CommitAsync();
        _logger.LogInformation("Policy flag created. Title: {Title}, EffectiveFrom: {EffectiveFrom:yyyy-MM-dd}",
            policyFlag.Title, policyFlag.EffectiveFrom);
        // Content-audit row (fail-open: UserActivityAudit swallows-and-logs, so a failed audit
        // write can never turn a committed create into an error).
        await _activityAudit.RecordPolicyFlagChangedAsync(
            request.ActingUserId, ContentChangeAction.Created, policyFlag.Title, cancellationToken);

        return Result<bool>.Success(true);
    }
}
