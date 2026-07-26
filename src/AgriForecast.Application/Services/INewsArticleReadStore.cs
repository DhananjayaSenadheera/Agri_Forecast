namespace AgriForecast.Application.Services;

// Read-only projection over the PYTHON-OWNED NewsArticles table (the RSS ingestion pipeline's capture
// store — the Python loader owns the DDL, so it is not part of the EF model and has no migration).
// The articles feed the ML sentiment features, not the curated NewsEvents CRUD; the two news stores stay
// separate on purpose.
// curated NewsEvents CRUD; the two "news" stores stay separate on purpose.
public interface INewsArticleReadStore
{
    // Latest articles, newest first by COALESCE(PublishedDateUtc, RetrievedAtUtc). Empty when the table has
    // no rows or does not exist yet — a missing table is a normal state, never an error.
    Task<IReadOnlyList<NewsArticleRow>> GetLatestAsync(int take, CancellationToken ct = default);
}

// One captured article. Url is the identity (the Python loader's key) and the timestamps are naive UTC.
// Topics and SentimentScore are the per-article signals the Python scorer writes back: Topics is a
// stable-order CSV of fired agri topic flags ('' = scored, none fired) and SentimentScore is the VADER
// compound in [-1,1]. Both are null when the article has not been scored yet.
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
