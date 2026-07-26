namespace AgriForecast.Application.Requests.NewsArticles.DTOs;

// Read shape for the admin News page's ingested-articles feed. Url doubles as the row identity (it is the
// Python capture table's key). Timestamps are naive UTC; publishedDateUtc is null when the feed omitted a
// publish date, and the FE falls back to retrievedAtUtc.
public class NewsArticle_GetDto
{
    public string Url { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public DateTime? PublishedDateUtc { get; set; }
    public DateTime RetrievedAtUtc { get; set; }
    public string Language { get; set; } = string.Empty;

    // Per-article signals from the Python scorer; null means not yet scored. Topics is a stable-order CSV
    // of fired agri topic flags ('' = scored, none fired) and sentimentScore is the VADER compound in
    // [-1,1]. The FE derives its badges from these — the wire carries facts, not presentation.
    public string? Topics { get; set; }
    public double? SentimentScore { get; set; }
}
