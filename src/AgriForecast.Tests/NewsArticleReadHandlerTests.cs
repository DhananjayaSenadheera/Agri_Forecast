using AgriForecast.Application.Requests.NewsArticles.Quaries.GetLatest;
using AgriForecast.Application.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Unit tests for the ingested-articles read handler. The DB is faked via a canned INewsArticleReadStore,
/// so the take clamp (default 50, max 200, never a 400), the row-to-DTO mapping and the empty -> 200 []
/// contract run in isolation. The store's raw SQL over the Python-owned table is not covered here, since
/// that table is deliberately outside the EF model and has no relational harness by design.
/// </summary>
public class NewsArticleReadHandlerTests
{
    // Fake store: records the take it was asked for and returns canned rows.
    private sealed class FakeStore : INewsArticleReadStore
    {
        public List<NewsArticleRow> Rows = new();
        public int? CapturedTake;

        public Task<IReadOnlyList<NewsArticleRow>> GetLatestAsync(int take, CancellationToken ct = default)
        {
            CapturedTake = take;
            IReadOnlyList<NewsArticleRow> rows = Rows.Take(take).ToList();
            return Task.FromResult(rows);
        }
    }

    private static NewsArticleGetLatestQueryHandler Handler(FakeStore store) =>
        new(store, Mock.Of<ILogger<NewsArticleGetLatestQueryHandler>>());

    private static NewsArticleRow Row(
        string url, DateTime? published = null, string? topics = "flood", double? sentiment = -0.42) =>
        new(url, "lbo", $"Title for {url}", "Summary", published,
            new DateTime(2026, 7, 22, 8, 0, 0), "en", topics, sentiment);

    [Fact]
    public async Task Maps_rows_to_dtos_verbatim()
    {
        var store = new FakeStore
        {
            Rows = { Row("https://example.com/a", new DateTime(2026, 7, 22, 11, 5, 6)) },
        };

        var result = await Handler(store).Handle(new NewsArticleGetLatestQuery(), default);

        result.IsSuccess.Should().BeTrue();
        var dto = result.Data.Should().ContainSingle().Subject;
        dto.Url.Should().Be("https://example.com/a");
        dto.Source.Should().Be("lbo");
        dto.Title.Should().Be("Title for https://example.com/a");
        dto.Summary.Should().Be("Summary");
        dto.PublishedDateUtc.Should().Be(new DateTime(2026, 7, 22, 11, 5, 6));
        dto.RetrievedAtUtc.Should().Be(new DateTime(2026, 7, 22, 8, 0, 0));
        dto.Language.Should().Be("en");
        dto.Topics.Should().Be("flood");
        dto.SentimentScore.Should().Be(-0.42);
    }

    [Fact]
    public async Task Unscored_article_signals_flow_through_as_null()
    {
        var store = new FakeStore
        {
            Rows = { Row("https://example.com/a", topics: null, sentiment: null) },
        };

        var result = await Handler(store).Handle(new NewsArticleGetLatestQuery(), default);

        var dto = result.Data.Single();
        dto.Topics.Should().BeNull();
        dto.SentimentScore.Should().BeNull();
    }

    [Fact]
    public async Task Null_publish_date_flows_through_as_null()
    {
        var store = new FakeStore { Rows = { Row("https://example.com/a", published: null) } };

        var result = await Handler(store).Handle(new NewsArticleGetLatestQuery(), default);

        result.Data.Single().PublishedDateUtc.Should().BeNull();
    }

    [Fact]
    public async Task Empty_store_is_success_with_empty_list()
    {
        var result = await Handler(new FakeStore()).Handle(new NewsArticleGetLatestQuery(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)] // absent
    [InlineData(0)]    // nonsense → clamp, never 400
    [InlineData(-5)]
    public async Task Absent_or_nonpositive_take_uses_default(int? take)
    {
        var store = new FakeStore();

        var result = await Handler(store).Handle(new NewsArticleGetLatestQuery { Take = take }, default);

        result.IsSuccess.Should().BeTrue();
        store.CapturedTake.Should().Be(NewsArticleGetLatestQueryHandler.DefaultTake);
    }

    [Fact]
    public async Task Take_above_max_clamps_to_max()
    {
        var store = new FakeStore();

        await Handler(store).Handle(new NewsArticleGetLatestQuery { Take = 5000 }, default);

        store.CapturedTake.Should().Be(NewsArticleGetLatestQueryHandler.MaxTake);
    }

    [Fact]
    public async Task Valid_take_is_passed_through()
    {
        var store = new FakeStore();

        await Handler(store).Handle(new NewsArticleGetLatestQuery { Take = 10 }, default);

        store.CapturedTake.Should().Be(10);
    }
}
