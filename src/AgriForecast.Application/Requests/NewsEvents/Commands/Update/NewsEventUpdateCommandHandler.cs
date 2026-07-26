using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.NewsEvents.Commands.Update;

public class NewsEventUpdateCommandHandler : IRequestHandler<NewsEventUpdateCommand, Result<Guid>>
{
    private readonly INewsEventRepository _newsEventRepository;
    private readonly ILogger<NewsEventUpdateCommandHandler> _logger;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IUserActivityAudit _activityAudit;

    public NewsEventUpdateCommandHandler(
        INewsEventRepository newsEventRepository,
        ILogger<NewsEventUpdateCommandHandler> logger,
        IUnitofWorkRepository unitofWorkRepository,
        IUserActivityAudit activityAudit)
    {
        _newsEventRepository = newsEventRepository;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _activityAudit = activityAudit;
    }

    public async Task<Result<Guid>> Handle(NewsEventUpdateCommand request, CancellationToken cancellationToken)
    {
        var dto = request.NewsEventUpdateDto;
        if (dto is null)
        {
            _logger.LogInformation("News event details are required.");
            return Result<Guid>.Failure("News event details are required.");
        }

        var existing = await _newsEventRepository.GetByIdAsync(dto.Id);
        if (existing is null)
        {
            _logger.LogInformation("Failed to update news event: id {Id} does not exist.", dto.Id);
            return Result<Guid>.Failure("News event does not exist.");
        }

        // Apply the scalar fields — PublishedAt is not among them, since the UpdateDto does not carry the
        // immutable vintage — then reconcile the crop/market links on the tracked graph.
        dto.ApplyTo(existing);
        _newsEventRepository.SetCropLinks(existing, dto.AffectedCropIds);
        _newsEventRepository.SetMarketLinks(existing, dto.AffectedMarketIds);
        await _unitofWorkRepository.CommitAsync();

        _logger.LogInformation("News event {Id} updated (PublishedAt preserved: {PublishedAt:yyyy-MM-dd}).",
            existing.Id, existing.PublishedAt);
        // Audited after the commit, and the audit swallows-and-logs, so it can never fail the update.
        await _activityAudit.RecordNewsEventChangedAsync(
            request.ActingUserId, ContentChangeAction.Updated, existing.Title, cancellationToken);

        return Result<Guid>.Success(existing.Id);
    }
}
