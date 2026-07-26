using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.NewsEvents.Commands.Delete;

public class NewsEventDeleteCommandHandler : IRequestHandler<NewsEventDeleteCommand, Result<Guid>>
{
    private readonly INewsEventRepository _newsEventRepository;
    private readonly ILogger<NewsEventDeleteCommandHandler> _logger;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IUserActivityAudit _activityAudit;

    public NewsEventDeleteCommandHandler(
        INewsEventRepository newsEventRepository,
        ILogger<NewsEventDeleteCommandHandler> logger,
        IUnitofWorkRepository unitofWorkRepository,
        IUserActivityAudit activityAudit)
    {
        _newsEventRepository = newsEventRepository;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _activityAudit = activityAudit;
    }

    public async Task<Result<Guid>> Handle(NewsEventDeleteCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == Guid.Empty)
        {
            _logger.LogInformation("Failed to delete news event: invalid (empty) id.");
            return Result<Guid>.Failure("Invalid news event id.");
        }

        var existing = await _newsEventRepository.GetByIdAsync(request.Id);
        if (existing is null)
        {
            _logger.LogInformation("Failed to delete news event: id {Id} does not exist.", request.Id);
            return Result<Guid>.Failure("News event does not exist.");
        }

        // The crop/market join rows are CASCADE-deleted with the event (DB-level FK).
        await _newsEventRepository.DeleteAsync(existing);
        await _unitofWorkRepository.CommitAsync();

        _logger.LogInformation("News event {Id} deleted.", existing.Id);
        // Audited after the commit, and the audit swallows-and-logs, so it can never fail the delete.
        await _activityAudit.RecordNewsEventChangedAsync(
            request.ActingUserId, ContentChangeAction.Deleted, existing.Title, cancellationToken);

        return Result<Guid>.Success(existing.Id);
    }
}
