using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.FestivalCalendar.Commands.Create;

public class FestivalCalendarCreateCommandHandler : IRequestHandler<FestivalCalendarCreateCommand, Result<bool>>
{
    private readonly IFestivalCalendarRepository _festivalCalendarRepository;
    private readonly ILogger<FestivalCalendarCreateCommandHandler> _logger;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IUserActivityAudit _activityAudit;

    public FestivalCalendarCreateCommandHandler(
        IFestivalCalendarRepository festivalCalendarRepository,
        ILogger<FestivalCalendarCreateCommandHandler> logger,
        IUnitofWorkRepository unitofWorkRepository,
        IUserActivityAudit activityAudit)
    {
        _festivalCalendarRepository = festivalCalendarRepository;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _activityAudit = activityAudit;
    }

    public async Task<Result<bool>> Handle(FestivalCalendarCreateCommand request, CancellationToken cancellationToken)
    {
        var dto = request.FestivalCalendarCreateDto;
        if (dto is null)
        {
            _logger.LogInformation("Festival details are required.");
            return Result<bool>.Failure("Festival details are required.");
        }

        // Guard the DB's UNIQUE (FestivalKey, Date) index up front so a duplicate is a 400, not a 500.
        if (await _festivalCalendarRepository.ExistsAsync(dto.FestivalKey, dto.Date.Date))
        {
            _logger.LogInformation(
                "Festival {Key} on {Date:yyyy-MM-dd} already exists.", dto.FestivalKey, dto.Date);
            return Result<bool>.Failure("A festival with this key already exists on this date.");
        }

        var entry = dto.ToEntity();
        await _festivalCalendarRepository.AddAsync(entry);
        await _unitofWorkRepository.CommitAsync();
        _logger.LogInformation("Festival created. Key: {Key}, Date: {Date:yyyy-MM-dd}",
            entry.FestivalKey, entry.Date);
        // Audited after the commit, and the audit swallows-and-logs, so it can never fail the create.
        await _activityAudit.RecordFestivalChangedAsync(
            request.ActingUserId, ContentChangeAction.Created, FestivalAuditIdentifier.For(entry.FestivalKey, entry.Date),
            cancellationToken);

        return Result<bool>.Success(true);
    }
}
