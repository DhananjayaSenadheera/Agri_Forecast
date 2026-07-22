using AgriForecast.Application.common;
using AgriForecast.Application.Requests.NewsArticles.DTOs;
using AgriForecast.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.NewsArticles.Quaries.GetLatest;

public class NewsArticleGetLatestQueryHandler
    : IRequestHandler<NewsArticleGetLatestQuery, Result<List<NewsArticle_GetDto>>>
{
    // Window discipline: a bad/absent take never rejects the request — it clamps. The feed is a
    // display window, not a query API; there is nothing an admin could "fix" about take=0.
    public const int DefaultTake = 50;
    public const int MaxTake = 200;

    private readonly INewsArticleReadStore _store;
    private readonly ILogger<NewsArticleGetLatestQueryHandler> _logger;

    public NewsArticleGetLatestQueryHandler(
        INewsArticleReadStore store,
        ILogger<NewsArticleGetLatestQueryHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<Result<List<NewsArticle_GetDto>>> Handle(
        NewsArticleGetLatestQuery request, CancellationToken cancellationToken)
    {
        var take = request.Take is > 0 ? Math.Min(request.Take.Value, MaxTake) : DefaultTake;

        var rows = await _store.GetLatestAsync(take, cancellationToken);

        // Empty is a normal state (fresh DB / ingestion not yet run) → 200 [].
        var dtos = rows.Select(r => new NewsArticle_GetDto
        {
            Url = r.Url,
            Source = r.Source,
            Title = r.Title,
            Summary = r.Summary,
            PublishedDateUtc = r.PublishedDateUtc,
            RetrievedAtUtc = r.RetrievedAtUtc,
            Language = r.Language,
            Topics = r.Topics,
            SentimentScore = r.SentimentScore,
        }).ToList();

        _logger.LogInformation("{Count} ingested news articles retrieved (take={Take}).", dtos.Count, take);
        return Result<List<NewsArticle_GetDto>>.Success(dtos);
    }
}
