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

        // An empty list is a normal state, so return 200 [] rather than the legacy 400-on-empty.
        var dtos = (entries ?? Enumerable.Empty<Domain.Entities.NewsEvent>()).ToGetDtoList();
        _logger.LogInformation("{Count} news events retrieved successfully.", dtos.Count);
        return Result<List<NewsEvent_GetDto>>.Success(dtos);
    }
}
