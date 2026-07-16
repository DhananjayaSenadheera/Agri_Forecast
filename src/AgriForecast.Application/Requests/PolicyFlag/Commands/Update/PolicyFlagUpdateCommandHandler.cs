using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.PolicyFlag.Commands.Update;

public class PolicyFlagUpdateCommandHandler
    : IRequestHandler<PolicyFlagUpdateCommand, Result<PolicyFlag_MutationResultDto>>
{
    private readonly IPolicyFlagRepository _policyFlagRepository;
    private readonly ILogger<PolicyFlagUpdateCommandHandler> _logger;
    private readonly IUnitofWorkRepository _unitofWorkRepository;

    public PolicyFlagUpdateCommandHandler(
        IPolicyFlagRepository policyFlagRepository,
        ILogger<PolicyFlagUpdateCommandHandler> logger,
        IUnitofWorkRepository unitofWorkRepository)
    {
        _policyFlagRepository = policyFlagRepository;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
    }

    public async Task<Result<PolicyFlag_MutationResultDto>> Handle(
        PolicyFlagUpdateCommand request, CancellationToken cancellationToken)
    {
        var dto = request.PolicyFlagUpdateDto;
        if (dto is null)
        {
            _logger.LogInformation("Policy flag details are required.");
            return Result<PolicyFlag_MutationResultDto>.Failure("Policy flag details are required.");
        }

        var existing = await _policyFlagRepository.GetByIdAsync(dto.Id);
        if (existing is null)
        {
            _logger.LogInformation("Failed to update policy flag: id {Id} does not exist.", dto.Id);
            return Result<PolicyFlag_MutationResultDto>.Failure("Policy flag does not exist.");
        }

        // Warn if EITHER the previous or the new window has already started (touches training history).
        var warning = PolicyFlagTrainingDataWarning.For(
            effectiveFrom: dto.EffectiveFrom,
            previousEffectiveFrom: existing.EffectiveFrom,
            nowUtc: DateTime.UtcNow);

        dto.ApplyTo(existing);
        await _policyFlagRepository.UpdateAsync(existing);
        await _unitofWorkRepository.CommitAsync();

        _logger.LogInformation(
            "Policy flag {Id} updated. EffectiveFrom: {EffectiveFrom:yyyy-MM-dd}, TrainingDataWarning: {HasWarning}",
            existing.Id, existing.EffectiveFrom, warning is not null);

        var data = new PolicyFlag_MutationResultDto { Id = existing.Id, TrainingDataWarning = warning };
        return warning is null
            ? Result<PolicyFlag_MutationResultDto>.Success(data)
            : Result<PolicyFlag_MutationResultDto>.SuccessWithWarnings(data, warning);
    }
}
