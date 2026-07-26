using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.NewsEvents.Commands.Create;

// Capture + storage only. NewsEvents are NOT yet ML feature inputs (the model will learn event
// weights in a later, separate task), so — unlike PolicyFlag / FestivalCalendar — there is
// deliberately NO training-data-warning here.
public class NewsEventCreateCommandHandler : IRequestHandler<NewsEventCreateCommand, Result<bool>>
{
    private readonly INewsEventRepository _newsEventRepository;
    private readonly ILogger<NewsEventCreateCommandHandler> _logger;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IUserActivityAudit _activityAudit;

    public NewsEventCreateCommandHandler(
        INewsEventRepository newsEventRepository,
        ILogger<NewsEventCreateCommandHandler> logger,
        IUnitofWorkRepository unitofWorkRepository,
        IUserActivityAudit activityAudit)
    {
        _newsEventRepository = newsEventRepository;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _activityAudit = activityAudit;
    }

    public async Task<Result<bool>> Handle(NewsEventCreateCommand request, CancellationToken cancellationToken)
    {
        var dto = request.NewsEventCreateDto;
        if (dto is null)
        {
            _logger.LogInformation("News event details are required.");
            return Result<bool>.Failure("News event details are required.");
        }

        var entity = dto.ToEntity();
        await _newsEventRepository.AddAsync(entity);
        await _unitofWorkRepository.CommitAsync();
        _logger.LogInformation(
            "News event created. Id: {Id}, PublishedAt: {PublishedAt:yyyy-MM-dd}, Crops: {Crops}, Markets: {Markets}",
            entity.Id, entity.PublishedAt, entity.AffectedCrops.Count, entity.AffectedMarkets.Count);
        // Content-audit row (fail-open: UserActivityAudit swallows-and-logs, so a failed audit
        // write can never turn a committed create into an error).
        await _activityAudit.RecordNewsEventChangedAsync(
            request.ActingUserId, ContentChangeAction.Created, entity.Title, cancellationToken);

        return Result<bool>.Success(true);
    }
}
