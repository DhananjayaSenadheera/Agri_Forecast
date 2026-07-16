using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.NewsEvents.DTOs;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.NewsEvents.Quaries.GetAll;

public class NewsEventGetAllQueryHandler
    : IRequestHandler<NewsEventGetAllQuery, Result<List<NewsEvent_GetDto>>>
{
    private readonly INewsEventRepository _newsEventRepository;
    private readonly ILogger<NewsEventGetAllQueryHandler> _logger;

    public NewsEventGetAllQueryHandler(
        INewsEventRepository newsEventRepository,
        ILogger<NewsEventGetAllQueryHandler> logger)
    {
        _newsEventRepository = newsEventRepository;
        _logger = logger;
    }

    public async Task<Result<List<NewsEvent_GetDto>>> Handle(
        NewsEventGetAllQuery request, CancellationToken cancellationToken)
    {
        var entries = await _newsEventRepository.GetAllAsync();

        // Empty list is a normal state → 200 [] (deliberately NOT the legacy policy-flag
        // 400-on-empty quirk — that was the API-10 review blocker; the admin News page renders
        // an empty list from a Success result).
        var dtos = (entries ?? Enumerable.Empty<Domain.Entities.NewsEvent>()).ToGetDtoList();
        _logger.LogInformation("{Count} news events retrieved successfully.", dtos.Count);
        return Result<List<NewsEvent_GetDto>>.Success(dtos);
    }
}
