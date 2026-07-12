using AgriForecast.Application.Requests.Forecast.Quaries.GetMarketOverview;
using AgriForecast.Application.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Unit tests for GetMarketOverviewQueryHandler. The DB is faked via a canned
/// IMarketOverviewStore so all business logic (day clamp, midpoint precedence,
/// movers, latest-prices + spark, empty-data shape) is exercised in isolation.
/// </summary>
public class MarketOverviewHandlerTests
{
    private static readonly Guid CropA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CropB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Mkt = Guid.Parse("b2a20001-0000-0000-0000-000000000001");
    private static readonly DateOnly AsOf = new(2026, 7, 10);

    private static GetMarketOverviewQueryHandler Build(FakeStore store)
        => new(store, Mock.Of<ILogger<GetMarketOverviewQueryHandler>>());

    // ---- Fake store -------------------------------------------------------

    private sealed class FakeStore : IMarketOverviewStore
    {
        public DateOnly? Latest;
        public List<MarketPriceWindowRow> Rows = new();
        public DateOnly? CapturedFrom;
        public DateOnly? CapturedTo;

        public Task<DateOnly?> GetLatestObservationDateAsync(CancellationToken ct = default)
            => Task.FromResult(Latest);

        public Task<IReadOnlyList<MarketPriceWindowRow>> GetRowsAsync(
            DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct = default)
        {
            CapturedFrom = fromInclusive;
            CapturedTo = toInclusive;
            IReadOnlyList<MarketPriceWindowRow> filtered = Rows
                .Where(r => r.Date >= fromInclusive && r.Date <= toInclusive)
                .ToList();
            return Task.FromResult(filtered);
        }
    }

    private static MarketPriceWindowRow Row(
        Guid cropId, string cropName, DateOnly date, decimal min, decimal max,
        Guid? mkt = null, string? mktName = null, decimal wholesale = 0m, decimal retail = 0m)
        => new(cropId, cropName, mkt ?? Mkt, mktName ?? "Dambulla Dedicated Economic Centre",
               date, min, max, wholesale, retail);

    // ---- Empty data -------------------------------------------------------

    [Fact]
    public async Task Empty_data_returns_null_asOf_and_empty_arrays()
    {
        var store = new FakeStore { Latest = null };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = 30 }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.AsOf.Should().BeNull();
        result.Data.WindowDays.Should().Be(30);
        result.Data.MarketsWithData.Should().Be(0);
        result.Data.CropsWithData.Should().Be(0);
        result.Data.Movers.Should().BeEmpty();
        result.Data.LatestPrices.Should().BeEmpty();
    }

    // ---- Day clamp --------------------------------------------------------

    [Theory]
    [InlineData(1, 7)]     // below min -> 7
    [InlineData(0, 7)]
    [InlineData(-5, 7)]
    [InlineData(7, 7)]
    [InlineData(30, 30)]
    [InlineData(90, 90)]
    [InlineData(500, 90)]  // above max -> 90
    public async Task Days_param_is_clamped(int requested, int expected)
    {
        var store = new FakeStore { Latest = AsOf };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = requested }, default);
        result.Data.WindowDays.Should().Be(expected);
    }

    [Fact]
    public async Task Clamped_min_still_fetches_14_days_for_sparkline()
    {
        // days=7 clamps to 7, but spark needs 14 days -> fetch window must be
        // 14 inclusive days ending at asOf (asOf-13 .. asOf).
        var store = new FakeStore { Latest = AsOf };
        await Build(store).Handle(new GetMarketOverviewQuery { Days = 7 }, default);
        store.CapturedFrom.Should().Be(AsOf.AddDays(-13));
        store.CapturedTo.Should().Be(AsOf);
    }

    // ---- Midpoint precedence ---------------------------------------------

    [Fact]
    public async Task Price_uses_midpoint_when_both_min_and_max_present()
    {
        var store = new FakeStore
        {
            Latest = AsOf,
            Rows = { Row(CropA, "Capsicum", AsOf, 500m, 580m) }
        };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = 30 }, default);
        var lp = result.Data.LatestPrices.Single();
        lp.Price.Should().Be(540m);      // (500+580)/2
        lp.MinPrice.Should().Be(500m);
        lp.MaxPrice.Should().Be(580m);
    }

    [Fact]
    public async Task Price_falls_back_to_max_when_min_is_zero()
    {
        var store = new FakeStore
        {
            Latest = AsOf,
            Rows = { Row(CropA, "Capsicum", AsOf, 0m, 580m) }
        };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = 30 }, default);
        result.Data.LatestPrices.Single().Price.Should().Be(580m);
    }

    [Fact]
    public async Task Price_falls_back_to_min_when_max_is_zero()
    {
        var store = new FakeStore
        {
            Latest = AsOf,
            Rows = { Row(CropA, "Capsicum", AsOf, 500m, 0m) }
        };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = 30 }, default);
        result.Data.LatestPrices.Single().Price.Should().Be(500m);
    }

    [Fact]
    public async Task Price_falls_back_to_wholesale_then_retail_when_band_absent()
    {
        var store = new FakeStore
        {
            Latest = AsOf,
            Rows =
            {
                // no Min/Max band -> Wholesale wins over Retail.
                Row(CropA, "Onion", AsOf, 0m, 0m, wholesale: 450m, retail: 300m),
                // no band, no wholesale -> Retail.
                Row(CropB, "Garlic", AsOf, 0m, 0m, wholesale: 0m, retail: 300m)
            }
        };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = 30 }, default);
        result.Data.LatestPrices.Single(l => l.CropId == CropA).Price.Should().Be(450m);
        result.Data.LatestPrices.Single(l => l.CropId == CropB).Price.Should().Be(300m);
    }

    // ---- Multi-market (PriceObservations lights up per (crop, market) pair) ----

    [Fact]
    public async Task Movers_are_per_crop_market_pair_across_multiple_markets()
    {
        var mkt2 = Guid.Parse("b2a20001-0000-0000-0000-000000000004");
        var store = new FakeStore
        {
            Latest = AsOf,
            Rows =
            {
                // Same crop, two markets, different trajectories.
                Row(CropA, "Beans", AsOf.AddDays(-8), 100m, 100m),                       // Dambulla prev 100
                Row(CropA, "Beans", AsOf,             150m, 150m),                       // Dambulla latest 150 -> +50%
                Row(CropA, "Beans", AsOf.AddDays(-8), 200m, 200m, mkt: mkt2, mktName: "Pettah (HARTI wholesale)"),
                Row(CropA, "Beans", AsOf,             160m, 160m, mkt: mkt2, mktName: "Pettah (HARTI wholesale)") // Pettah 200->160 = -20%
            }
        };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = 30 }, default);

        var movers = result.Data.Movers;
        movers.Should().HaveCount(2);
        movers.Select(m => m.MarketName).Distinct().Should().HaveCount(2);
        var dambulla = movers.Single(m => m.MarketName.StartsWith("Dambulla"));
        var pettah = movers.Single(m => m.MarketName.StartsWith("Pettah"));
        dambulla.Direction.Should().Be("up");
        dambulla.ChangePct.Should().Be(50.0m);
        pettah.Direction.Should().Be("down");
        pettah.ChangePct.Should().Be(-20.0m);
    }

    // ---- Movers -----------------------------------------------------------

    [Fact]
    public async Task Mover_is_computed_with_up_direction_and_rounded_changePct()
    {
        var store = new FakeStore
        {
            Latest = AsOf,
            Rows =
            {
                Row(CropA, "Capsicum", AsOf.AddDays(-8), 460m, 500m), // prev midpoint 480
                Row(CropA, "Capsicum", AsOf,             520m, 584m)  // latest midpoint 552
            }
        };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = 30 }, default);
        var m = result.Data.Movers.Single();
        m.LatestPrice.Should().Be(552m);
        m.PreviousPrice.Should().Be(480m);
        m.ChangePct.Should().Be(15.0m);   // (552-480)/480*100
        m.Direction.Should().Be("up");
    }

    [Fact]
    public async Task Mover_direction_is_down_for_a_faller()
    {
        var store = new FakeStore
        {
            Latest = AsOf,
            Rows =
            {
                Row(CropA, "Beans", AsOf.AddDays(-8), 600m, 600m), // prev 600
                Row(CropA, "Beans", AsOf,             480m, 480m)  // latest 480 -> -20%
            }
        };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = 30 }, default);
        var m = result.Data.Movers.Single();
        m.Direction.Should().Be("down");
        m.ChangePct.Should().Be(-20.0m);
    }

    [Fact]
    public async Task Mover_excluded_when_no_previous_point_within_lag()
    {
        // Only a latest point (and a point < 7 days before) -> no valid baseline.
        var store = new FakeStore
        {
            Latest = AsOf,
            Rows =
            {
                Row(CropA, "Leeks", AsOf.AddDays(-2), 500m, 500m),
                Row(CropA, "Leeks", AsOf,             560m, 560m)
            }
        };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = 30 }, default);
        result.Data.Movers.Should().BeEmpty();
        result.Data.LatestPrices.Should().NotBeEmpty(); // still surfaces as a latest price
    }

    [Fact]
    public async Task Movers_return_top_risers_then_fallers_each_by_abs_change()
    {
        var rows = new List<MarketPriceWindowRow>();
        // 6 risers with ascending change, 6 fallers with descending change.
        for (int i = 1; i <= 6; i++)
        {
            var crop = Guid.NewGuid();
            rows.Add(Row(crop, $"Rise{i}", AsOf.AddDays(-8), 100m, 100m));
            rows.Add(Row(crop, $"Rise{i}", AsOf, 100m + i * 10m, 100m + i * 10m)); // +10i%
        }
        for (int i = 1; i <= 6; i++)
        {
            var crop = Guid.NewGuid();
            rows.Add(Row(crop, $"Fall{i}", AsOf.AddDays(-8), 100m, 100m));
            rows.Add(Row(crop, $"Fall{i}", AsOf, 100m - i * 5m, 100m - i * 5m)); // -5i%
        }

        var store = new FakeStore { Latest = AsOf, Rows = rows };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = 30 }, default);

        var movers = result.Data.Movers;
        movers.Count(m => m.Direction == "up").Should().Be(5);
        movers.Count(m => m.Direction == "down").Should().Be(5);
        // Risers ordered by |change| desc, and come before fallers.
        var ups = movers.TakeWhile(m => m.Direction == "up").ToList();
        ups.Should().BeInDescendingOrder(m => m.ChangePct);
        ups.First().ChangePct.Should().Be(60.0m); // Rise6
        // Fallers ordered by most-negative first.
        var downs = movers.SkipWhile(m => m.Direction == "up").ToList();
        downs.First().ChangePct.Should().Be(-30.0m); // Fall6
    }

    // ---- Latest prices ----------------------------------------------------

    [Fact]
    public async Task LatestPrices_one_row_per_crop_capped_at_8_with_spark()
    {
        var rows = new List<MarketPriceWindowRow>();
        // CropA has a 3-point recent series for the sparkline.
        rows.Add(Row(CropA, "Capsicum", AsOf.AddDays(-4), 500m, 520m));
        rows.Add(Row(CropA, "Capsicum", AsOf.AddDays(-2), 510m, 530m));
        rows.Add(Row(CropA, "Capsicum", AsOf, 520m, 540m));
        rows.Add(Row(CropB, "Beans", AsOf.AddDays(-1), 300m, 320m));

        var store = new FakeStore { Latest = AsOf, Rows = rows };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = 30 }, default);

        result.Data.LatestPrices.Should().HaveCount(2);
        var capsicum = result.Data.LatestPrices.Single(l => l.CropId == CropA);
        capsicum.Date.Should().Be("2026-07-10");
        capsicum.Spark.Should().HaveCount(3);
        capsicum.Spark.Select(s => s.Date).Should().BeInAscendingOrder();
        // Freshest crop first (both fresh here; CropA is on asOf, CropB one day earlier).
        result.Data.LatestPrices.First().CropId.Should().Be(CropA);
    }

    [Fact]
    public async Task Dates_are_yyyy_MM_dd_strings_and_counts_reflect_window()
    {
        var store = new FakeStore
        {
            Latest = AsOf,
            Rows =
            {
                Row(CropA, "Capsicum", AsOf, 500m, 520m),
                Row(CropB, "Beans", AsOf.AddDays(-3), 300m, 320m)
            }
        };
        var result = await Build(store).Handle(new GetMarketOverviewQuery { Days = 30 }, default);
        result.Data.AsOf.Should().Be("2026-07-10");
        result.Data.CropsWithData.Should().Be(2);
        result.Data.MarketsWithData.Should().Be(1);
    }
}
