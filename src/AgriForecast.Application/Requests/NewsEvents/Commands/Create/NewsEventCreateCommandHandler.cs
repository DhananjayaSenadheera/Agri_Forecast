using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
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

    public NewsEventCreateCommandHandler(
        INewsEventRepository newsEventRepository,
        ILogger<NewsEventCreateCommandHandler> logger,
        IUnitofWorkRepository unitofWorkRepository)
    {
        _newsEventRepository = newsEventRepository;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
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
        return Result<bool>.Success(true);
    }
}
