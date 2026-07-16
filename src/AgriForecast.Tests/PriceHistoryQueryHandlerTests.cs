using AgriForecast.Application.Requests.Prices.Quaries.GetHistory;
using AgriForecast.Application.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Unit tests for GetPriceHistoryQueryHandler (API-2). The DB is faked via a canned
/// IPriceHistoryStore so the window clamp + fence-post, per-date min/max aggregation, the
/// fail-closed exclusion of unusable-band rows, and the empty-result shape are exercised
/// in isolation. NOT covered here: the store's SQL filters (IsUnitConfirmed=1,
/// MinPrice/MaxPrice > 0, market scope, NationalAggregate exclusion) — no relational
/// test provider is referenced, so they are verified by inspection + a manual live check
/// only; this fake merely reproduces the store's contract (usable rows only, coalesced).
/// </summary>
public class PriceHistoryQueryHandlerTests
{
    private static readonly Guid Crop = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid MktA = Guid.Parse("aaaa0001-0000-0000-0000-000000000001");
    private static readonly DateOnly Latest = new(2026, 7, 10);

    private static GetPriceHistoryQueryHandler Build(FakeStore store)
        => new(store, Mock.Of<ILogger<GetPriceHistoryQueryHandler>>());

    private sealed class FakeStore : IPriceHistoryStore
    {
        public DateOnly? LatestDate;
        public List<PriceHistoryRow> Rows = new();
        public DateOnly? CapturedFrom;
        public DateOnly? CapturedTo;
        public Guid? CapturedMarketId;

        public Task<DateOnly?> GetLatestObservedDateAsync(
            Guid cropId, Guid? marketId, CancellationToken ct = default)
            => Task.FromResult(LatestDate);

        public Task<IReadOnlyList<PriceHistoryRow>> GetRowsAsync(
            Guid cropId, Guid? marketId, DateOnly fromInclusive, DateOnly toInclusive,
            CancellationToken ct = default)
        {
            CapturedFrom = fromInclusive;
            CapturedTo = toInclusive;
            CapturedMarketId = marketId;
            IReadOnlyList<PriceHistoryRow> rows = Rows
                .Where(r => r.Date >= fromInclusive && r.Date <= toInclusive)
                .ToList();
            return Task.FromResult(rows);
        }
    }

    private static PriceHistoryRow Row(DateOnly d, decimal min, decimal max) => new(d, min, max);

    // ---- Empty ------------------------------------------------------------

    [Fact]
    public async Task No_data_returns_success_with_empty_list()
    {
        var store = new FakeStore { LatestDate = null };
        var result = await Build(store).Handle(
            new GetPriceHistoryQuery { CropId = Crop }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }

    // ---- Window clamp -----------------------------------------------------

    [Theory]
    [InlineData(1, 7)]      // below min -> 7
    [InlineData(0, 7)]
    [InlineData(-5, 7)]
    [InlineData(7, 7)]
    [InlineData(90, 90)]
    [InlineData(365, 365)]
    [InlineData(1000, 365)] // above max -> 365
    public async Task Days_param_is_clamped_and_fetch_window_matches(int requested, int expectedDays)
    {
        var store = new FakeStore { LatestDate = Latest };
        await Build(store).Handle(
            new GetPriceHistoryQuery { CropId = Crop, Days = requested }, default);

        // Fence-post: inclusive window ending at Latest is exactly expectedDays days,
        // i.e. from = Latest - (expectedDays - 1).
        store.CapturedTo.Should().Be(Latest);
        store.CapturedFrom.Should().Be(Latest.AddDays(-(expectedDays - 1)));
    }

    [Fact]
    public async Task Default_window_is_90_days()
    {
        var store = new FakeStore { LatestDate = Latest };
        await Build(store).Handle(new GetPriceHistoryQuery { CropId = Crop }, default);
        store.CapturedFrom.Should().Be(Latest.AddDays(-89));
    }

    // ---- marketId threading ----------------------------------------------

    [Fact]
    public async Task MarketId_is_threaded_to_store()
    {
        var store = new FakeStore { LatestDate = Latest };
        await Build(store).Handle(
            new GetPriceHistoryQuery { CropId = Crop, MarketId = MktA }, default);
        store.CapturedMarketId.Should().Be(MktA);
    }

    // ---- Per-date min/max aggregation ------------------------------------

    [Fact]
    public async Task Aggregates_min_of_mins_and_max_of_maxes_per_date()
    {
        var d = new DateOnly(2026, 7, 9);
        var store = new FakeStore
        {
            LatestDate = Latest,
            Rows =
            {
                Row(d, 400m, 500m), // market/source 1
                Row(d, 380m, 460m), // market/source 2 (lower low)
                Row(d, 420m, 540m), // market/source 3 (higher high)
                Row(Latest, 450m, 520m),
            }
        };

        var result = await Build(store).Handle(
            new GetPriceHistoryQuery { CropId = Crop, Days = 30 }, default);

        result.Data.Should().HaveCount(2);
        var day = result.Data.Single(p => p.Date == "2026-07-09");
        day.MinPrice.Should().Be(380m); // min of the day's mins
        day.MaxPrice.Should().Be(540m); // max of the day's maxes
    }

    [Fact]
    public async Task Points_are_chronological()
    {
        var store = new FakeStore
        {
            LatestDate = Latest,
            Rows =
            {
                Row(new DateOnly(2026, 7, 10), 1m, 2m),
                Row(new DateOnly(2026, 7, 8), 1m, 2m),
                Row(new DateOnly(2026, 7, 9), 1m, 2m),
            }
        };

        var result = await Build(store).Handle(
            new GetPriceHistoryQuery { CropId = Crop, Days = 30 }, default);

        result.Data.Select(p => p.Date).Should()
            .ContainInOrder("2026-07-08", "2026-07-09", "2026-07-10");
    }

    // ---- Fail-closed exclusion (defense-in-depth) ------------------------

    [Fact]
    public async Task Rows_without_a_usable_band_are_excluded_not_invented()
    {
        var d1 = new DateOnly(2026, 7, 9);
        var store = new FakeStore
        {
            LatestDate = Latest,
            Rows =
            {
                Row(d1, 0m, 500m),   // absent low  -> excluded
                Row(Latest, 400m, 0m), // absent high -> excluded
            }
        };

        var result = await Build(store).Handle(
            new GetPriceHistoryQuery { CropId = Crop, Days = 30 }, default);

        // Both rows dropped: no fabricated range, empty series.
        result.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Usable_row_survives_alongside_unusable_row_on_same_date()
    {
        var d = new DateOnly(2026, 7, 9);
        var store = new FakeStore
        {
            LatestDate = Latest,
            Rows =
            {
                Row(d, 0m, 500m),   // unusable -> dropped
                Row(d, 410m, 470m), // usable -> defines the day
            }
        };

        var result = await Build(store).Handle(
            new GetPriceHistoryQuery { CropId = Crop, Days = 30 }, default);

        var day = result.Data.Single();
        day.MinPrice.Should().Be(410m);
        day.MaxPrice.Should().Be(470m);
    }
}
