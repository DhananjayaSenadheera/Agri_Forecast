namespace AgriForecast.Application.Services;

// Read-only projection over the PYTHON-OWNED NewsArticles table (the RSS ingestion pipeline's
// capture store — agriforecast_ml/news/loader.py owns the DDL; it is deliberately NOT part of
// the EF model and has no migration). Thin DB seam so the GetLatest handler is unit-testable
// with canned rows (mirrors IIndicatorReadStore / IMarketReadStore).
//
// This surfaces what ingestion actually captured to the admin News page — read-only,
// display-only. The articles feed the ML sentiment features (NewsSentimentDaily), NOT the
// curated NewsEvents CRUD; the two "news" stores stay separate on purpose.
public interface INewsArticleReadStore
{
    // Latest articles, newest first (COALESCE(PublishedDateUtc, RetrievedAtUtc) DESC — a feed
    // that omits a publish date still sorts by when we fetched it). Empty when the table has no
    // rows OR does not exist yet (fresh DB where the Python ingestion never ran) — the missing
    // table is a normal state, never an error.
    Task<IReadOnlyList<NewsArticleRow>> GetLatestAsync(int take, CancellationToken ct = default);
}

// One captured article. Url is the identity (the Python loader's PK / dedupe key).
// PublishedDateUtc/RetrievedAtUtc are naive UTC wall-clock (the Python loader's convention).
//
// Topics/SentimentScore are the PER-ARTICLE signals the Python scorer writes back
// (score_news.py, on every ingest-news pass): Topics is a stable-order CSV of fired agri
// topic flags (pest,flood,drought,policy,fertiliser,import_ban), '' = scored but no topic;
// SentimentScore is the VADER compound in [-1,1]. BOTH are null when the article has not
// been scored yet (captured after the last scoring pass, or a pre-signal DB).
public sealed record NewsArticleRow(
    string Url,
    string Source,
    string Title,
    string Summary,
    DateTime? PublishedDateUtc,
    DateTime RetrievedAtUtc,
    string Language,
    string? Topics,
    double? SentimentScore);
