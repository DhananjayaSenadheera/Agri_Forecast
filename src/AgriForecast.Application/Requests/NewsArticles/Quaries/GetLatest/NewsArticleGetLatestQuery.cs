using AgriForecast.Application.common;
using AgriForecast.Application.Requests.NewsArticles.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.NewsArticles.Quaries.GetLatest;

// Latest ingested news articles, newest first. Take is optional; the handler applies the default and cap
// because the capture table grows without bound.
public class NewsArticleGetLatestQuery : IRequest<Result<List<NewsArticle_GetDto>>>
{
    public int? Take { get; set; }
}
