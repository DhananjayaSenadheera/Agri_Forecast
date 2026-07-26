using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.NewsEvents.Commands.Create;

// Capture and storage only. NewsEvents are not ML feature inputs yet, so — unlike PolicyFlag and
// FestivalCalendar — there is deliberately no training-data warning here.
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
        // Audited after the commit, and the audit swallows-and-logs, so it can never fail the create.
        await _activityAudit.RecordNewsEventChangedAsync(
            request.ActingUserId, ContentChangeAction.Created, entity.Title, cancellationToken);

        return Result<bool>.Success(true);
    }
}
