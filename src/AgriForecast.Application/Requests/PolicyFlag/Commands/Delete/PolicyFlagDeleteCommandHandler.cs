using AgriForecast.Application.common;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.PolicyFlag.Commands.Delete;

public class PolicyFlagDeleteCommandHandler
    : IRequestHandler<PolicyFlagDeleteCommand, Result<PolicyFlag_MutationResultDto>>
{
    private readonly IPolicyFlagRepository _policyFlagRepository;
    private readonly ILogger<PolicyFlagDeleteCommandHandler> _logger;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IUserActivityAudit _activityAudit;

    public PolicyFlagDeleteCommandHandler(
        IPolicyFlagRepository policyFlagRepository,
        ILogger<PolicyFlagDeleteCommandHandler> logger,
        IUnitofWorkRepository unitofWorkRepository,
        IUserActivityAudit activityAudit)
    {
        _policyFlagRepository = policyFlagRepository;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _activityAudit = activityAudit;
    }

    public async Task<Result<PolicyFlag_MutationResultDto>> Handle(
        PolicyFlagDeleteCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == Guid.Empty)
        {
            _logger.LogInformation("Failed to delete policy flag: invalid (empty) id.");
            return Result<PolicyFlag_MutationResultDto>.Failure("Invalid policy flag id.");
        }

        var existing = await _policyFlagRepository.GetByIdAsync(request.Id);
        if (existing is null)
        {
            _logger.LogInformation("Failed to delete policy flag: id {Id} does not exist.", request.Id);
            return Result<PolicyFlag_MutationResultDto>.Failure("Policy flag does not exist.");
        }

        // Deleting a flag whose window has already started removes it from history the model trained on.
        var warning = PolicyFlagTrainingDataWarning.For(
            effectiveFrom: existing.EffectiveFrom,
            previousEffectiveFrom: existing.EffectiveFrom,
            nowUtc: DateTime.UtcNow);

        await _policyFlagRepository.DeleteAsync(existing);
        await _unitofWorkRepository.CommitAsync();

        _logger.LogInformation(
            "Policy flag {Id} deleted. EffectiveFrom: {EffectiveFrom:yyyy-MM-dd}, TrainingDataWarning: {HasWarning}",
            existing.Id, existing.EffectiveFrom, warning is not null);

        // Content-audit row (fail-open: UserActivityAudit swallows-and-logs, so a failed audit
        // write can never turn a committed delete into an error).
        await _activityAudit.RecordPolicyFlagChangedAsync(
            request.ActingUserId, ContentChangeAction.Deleted, existing.Title, cancellationToken);

        var data = new PolicyFlag_MutationResultDto { Id = existing.Id, TrainingDataWarning = warning };
        return warning is null
            ? Result<PolicyFlag_MutationResultDto>.Success(data)
            : Result<PolicyFlag_MutationResultDto>.SuccessWithWarnings(data, warning);
    }
}
