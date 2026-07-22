using AgriForecast.Application.common;
using AgriForecast.Application.Requests.NewsArticles.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.NewsArticles.Quaries.GetLatest;

// Latest ingested news articles (the Python RSS pipeline's capture store), newest first.
// Take is optional — the handler applies the default/cap (a windowed feed, deliberately NOT
// get/all: the capture table grows without bound).
public class NewsArticleGetLatestQuery : IRequest<Result<List<NewsArticle_GetDto>>>
{
    public int? Take { get; set; }
}
