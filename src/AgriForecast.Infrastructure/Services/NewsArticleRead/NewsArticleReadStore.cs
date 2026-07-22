using AgriForecast.Application.Services;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Services.NewsArticleRead;

// Read-only store over the PYTHON-OWNED NewsArticles table (agriforecast_ml/news/loader.py owns
// the DDL). The table is deliberately NOT in the EF model — no entity, no migration, no snapshot
// entry — so this store reads it with Database.SqlQuery instead of a DbSet. That keeps the
// ownership boundary honest: EF migrations can never try to create/alter a table Python creates.
//
// FAIL-SOFT: on a fresh DB where the news ingestion has never run, the table does not exist.
// That is a normal state (same posture as an empty curated-events list), so we probe
// INFORMATION_SCHEMA first and return [] rather than letting SqlException 208 become a 500.
public class NewsArticleReadStore : INewsArticleReadStore
{
    private readonly AgriForecastDbContext _db;

    public NewsArticleReadStore(AgriForecastDbContext db) => _db = db;

    public async Task<IReadOnlyList<NewsArticleRow>> GetLatestAsync(int take, CancellationToken ct = default)
    {
        var tableExists = await _db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS [Value] FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'NewsArticles'")
            .FirstAsync(ct) > 0;
        if (!tableExists) return Array.Empty<NewsArticleRow>();

        // The row limit is parameterized by the interpolation (never string-concatenated).
        // COALESCE: articles whose feed omitted a publish date still sort by fetch time.
        var raw = await _db.Database
            .SqlQuery<SqlRow>($@"
SELECT [Url], [Source], [Title], [Summary], [PublishedDateUtc], [RetrievedAtUtc], [Language]
FROM [NewsArticles]
ORDER BY COALESCE([PublishedDateUtc], [RetrievedAtUtc]) DESC, [Url]
OFFSET 0 ROWS FETCH NEXT {take} ROWS ONLY")
            .ToListAsync(ct);

        return raw
            .Select(x => new NewsArticleRow(
                x.Url, x.Source, x.Title, x.Summary, x.PublishedDateUtc, x.RetrievedAtUtc, x.Language))
            .ToList();
    }

    // SqlQuery materialization target: mapped by column name, needs settable properties.
    private sealed class SqlRow
    {
        public string Url { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public DateTime? PublishedDateUtc { get; set; }
        public DateTime RetrievedAtUtc { get; set; }
        public string Language { get; set; } = string.Empty;
    }
}
