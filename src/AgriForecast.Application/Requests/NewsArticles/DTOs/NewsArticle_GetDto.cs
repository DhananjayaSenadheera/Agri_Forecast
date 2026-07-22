namespace AgriForecast.Application.Requests.NewsArticles.DTOs;

// Read shape for the admin News page's "Ingested articles" feed. Matches the FE NewsArticle
// interface (ForecastUI src/api/types.ts) — camelCase on the wire. Url doubles as the row
// identity (it is the Python capture table's PK / dedupe key). Timestamps are naive UTC
// wall-clock (the Python loader's convention); publishedDateUtc is null when the source feed
// omitted a publish date (the FE falls back to retrievedAtUtc for display).
public class NewsArticle_GetDto
{
    public string Url { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTime? PublishedDateUtc { get; set; }
    public DateTime RetrievedAtUtc { get; set; }
    public string Language { get; set; } = string.Empty;

    // Per-article signals from the Python scorer (null = not yet scored). topics is a
    // stable-order CSV of fired agri topic flags ('' = scored, none fired); sentimentScore
    // is the VADER compound in [-1,1]. The FE derives the farmer-facing category badge and
    // bullish/bearish glyph from these — the wire carries facts, not presentation.
    public string? Topics { get; set; }
    public double? SentimentScore { get; set; }
}
