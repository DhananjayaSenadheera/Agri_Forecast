using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.FestivalCalendar.Commands.Update;

public class FestivalCalendarUpdateCommandHandler
    : IRequestHandler<FestivalCalendarUpdateCommand, Result<FestivalCalendar_MutationResultDto>>
{
    private readonly IFestivalCalendarRepository _festivalCalendarRepository;
    private readonly ILogger<FestivalCalendarUpdateCommandHandler> _logger;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IUserActivityAudit _activityAudit;

    public FestivalCalendarUpdateCommandHandler(
        IFestivalCalendarRepository festivalCalendarRepository,
        ILogger<FestivalCalendarUpdateCommandHandler> logger,
        IUnitofWorkRepository unitofWorkRepository,
        IUserActivityAudit activityAudit)
    {
        _festivalCalendarRepository = festivalCalendarRepository;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _activityAudit = activityAudit;
    }

    public async Task<Result<FestivalCalendar_MutationResultDto>> Handle(
        FestivalCalendarUpdateCommand request, CancellationToken cancellationToken)
    {
        var dto = request.FestivalCalendarUpdateDto;
        if (dto is null)
        {
            _logger.LogInformation("Festival details are required.");
            return Result<FestivalCalendar_MutationResultDto>.Failure("Festival details are required.");
        }

        var existing = await _festivalCalendarRepository.GetByIdAsync(dto.Id);
        if (existing is null)
        {
            _logger.LogInformation("Failed to update festival: id {Id} does not exist.", dto.Id);
            return Result<FestivalCalendar_MutationResultDto>.Failure("Festival does not exist.");
        }

        // Guard the UNIQUE (FestivalKey, Date) index; excludeId lets the row keep its own key and date.
        if (await _festivalCalendarRepository.ExistsAsync(dto.FestivalKey, dto.Date.Date, dto.Id))
        {
            _logger.LogInformation(
                "Failed to update festival {Id}: {Key} on {Date:yyyy-MM-dd} already exists.",
                dto.Id, dto.FestivalKey, dto.Date);
            return Result<FestivalCalendar_MutationResultDto>.Failure(
                "A festival with this key already exists on this date.");
        }

        // Warn if EITHER the previous or the new Date is in the past (touches training history).
        var warning = FestivalCalendarTrainingDataWarning.For(
            date: dto.Date,
            previousDate: existing.Date,
            nowUtc: DateTime.UtcNow);

        dto.ApplyTo(existing);
        await _festivalCalendarRepository.UpdateAsync(existing);
        await _unitofWorkRepository.CommitAsync();

        _logger.LogInformation(
            "Festival {Id} updated. Date: {Date:yyyy-MM-dd}, TrainingDataWarning: {HasWarning}",
            existing.Id, existing.Date, warning is not null);

        // Audited after the commit, and the audit swallows-and-logs, so it can never fail the update.
        await _activityAudit.RecordFestivalChangedAsync(
            request.ActingUserId, ContentChangeAction.Updated, FestivalAuditIdentifier.For(existing.FestivalKey, existing.Date),
            cancellationToken);

        var data = new FestivalCalendar_MutationResultDto { Id = existing.Id, TrainingDataWarning = warning };
        return warning is null
            ? Result<FestivalCalendar_MutationResultDto>.Success(data)
            : Result<FestivalCalendar_MutationResultDto>.SuccessWithWarnings(data, warning);
    }
}
