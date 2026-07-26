using AgriForecast.Application.common;
using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.FestivalCalendar.Commands.Delete;

public class FestivalCalendarDeleteCommandHandler
    : IRequestHandler<FestivalCalendarDeleteCommand, Result<FestivalCalendar_MutationResultDto>>
{
    private readonly IFestivalCalendarRepository _festivalCalendarRepository;
    private readonly ILogger<FestivalCalendarDeleteCommandHandler> _logger;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IUserActivityAudit _activityAudit;

    public FestivalCalendarDeleteCommandHandler(
        IFestivalCalendarRepository festivalCalendarRepository,
        ILogger<FestivalCalendarDeleteCommandHandler> logger,
        IUnitofWorkRepository unitofWorkRepository,
        IUserActivityAudit activityAudit)
    {
        _festivalCalendarRepository = festivalCalendarRepository;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _activityAudit = activityAudit;
    }

    public async Task<Result<FestivalCalendar_MutationResultDto>> Handle(
        FestivalCalendarDeleteCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == Guid.Empty)
        {
            _logger.LogInformation("Failed to delete festival: invalid (empty) id.");
            return Result<FestivalCalendar_MutationResultDto>.Failure("Invalid festival id.");
        }

        var existing = await _festivalCalendarRepository.GetByIdAsync(request.Id);
        if (existing is null)
        {
            _logger.LogInformation("Failed to delete festival: id {Id} does not exist.", request.Id);
            return Result<FestivalCalendar_MutationResultDto>.Failure("Festival does not exist.");
        }

        // Deleting a festival whose Date is already in the past removes it from the lead-up windows
        // the model trained on (pass the stored Date as both arguments).
        var warning = FestivalCalendarTrainingDataWarning.For(
            date: existing.Date,
            previousDate: existing.Date,
            nowUtc: DateTime.UtcNow);

        await _festivalCalendarRepository.DeleteAsync(existing);
        await _unitofWorkRepository.CommitAsync();

        _logger.LogInformation(
            "Festival {Id} deleted. Date: {Date:yyyy-MM-dd}, TrainingDataWarning: {HasWarning}",
            existing.Id, existing.Date, warning is not null);

        // Content-audit row (fail-open: UserActivityAudit swallows-and-logs, so a failed audit
        // write can never turn a committed delete into an error).
        await _activityAudit.RecordFestivalChangedAsync(
            request.ActingUserId, ContentChangeAction.Deleted, FestivalAuditIdentifier.For(existing.FestivalKey, existing.Date),
            cancellationToken);

        var data = new FestivalCalendar_MutationResultDto { Id = existing.Id, TrainingDataWarning = warning };
        return warning is null
            ? Result<FestivalCalendar_MutationResultDto>.Success(data)
            : Result<FestivalCalendar_MutationResultDto>.SuccessWithWarnings(data, warning);
    }
}
