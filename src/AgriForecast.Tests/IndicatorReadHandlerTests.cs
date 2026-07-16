using AgriForecast.Application.Requests.Indicators.Quaries.GetIndicatorSeries;
using AgriForecast.Application.Requests.Indicators.Quaries.GetMacroSeries;
using AgriForecast.Application.Requests.Indicators.Quaries.GetSeriesCatalog;
using AgriForecast.Application.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// API-11 — unit tests for the three read handlers (GetIndicatorSeries / GetMacroSeries /
/// GetSeriesCatalog). The DB is faked via a canned IIndicatorReadStore so window resolution,
/// from>to rejection, the two-date discipline, catalog shape, and the empty->200-[] contract
/// are exercised in isolation. NOT covered here: the store's EF LINQ (verified by build +
/// live DB spot-check). If a relational harness ever lands, the date-inclusivity fence-post
/// belongs on the store there.
/// </summary>
public class IndicatorReadHandlerTests
{
    // ── Fake store: records the window it was asked for, returns canned rows ──────────
    private sealed class FakeStore : IIndicatorReadStore
    {
        public DateOnly? IndicatorLatest;
        public DateOnly? MacroLatest;
        public List<IndicatorPointRow> IndicatorRows = new();
        public List<MacroPointRow> MacroRows = new();
        public List<SeriesCatalogRow> Catalog = new();

        public string? CapturedIndicatorCode;
        public string? CapturedMacroKey;
        public (DateOnly From, DateOnly To)? CapturedIndicatorWindow;
        public (DateOnly From, DateOnly To)? CapturedMacroWindow;

        public Task<DateOnly?> GetLatestIndicatorDateAsync(string code, CancellationToken ct = default)
            => Task.FromResult(IndicatorLatest);

        public Task<IReadOnlyList<IndicatorPointRow>> GetIndicatorRowsAsync(
            string code, DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            CapturedIndicatorCode = code;
            CapturedIndicatorWindow = (from, to);
            IReadOnlyList<IndicatorPointRow> rows =
                IndicatorRows.Where(r => r.Date >= from && r.Date <= to).ToList();
            return Task.FromResult(rows);
        }

        public Task<DateOnly?> GetLatestMacroReferenceDateAsync(string key, CancellationToken ct = default)
            => Task.FromResult(MacroLatest);

        public Task<IReadOnlyList<MacroPointRow>> GetMacroRowsAsync(
            string key, DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            CapturedMacroKey = key;
            CapturedMacroWindow = (from, to);
            // Model the store's ReferenceDate-only window (PublishedAt never filters).
            IReadOnlyList<MacroPointRow> rows =
                MacroRows.Where(r => r.ReferenceDate >= from && r.ReferenceDate <= to).ToList();
            return Task.FromResult(rows);
        }

        public Task<IReadOnlyList<SeriesCatalogRow>> GetCatalogAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SeriesCatalogRow>>(Catalog);
    }

    private static GetIndicatorSeriesQueryHandler Indicators(FakeStore s)
        => new(s, Mock.Of<ILogger<GetIndicatorSeriesQueryHandler>>());
    private static GetMacroSeriesQueryHandler Macro(FakeStore s)
        => new(s, Mock.Of<ILogger<GetMacroSeriesQueryHandler>>());
    private static GetSeriesCatalogQueryHandler CatalogH(FakeStore s) => new(s);

    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    // ══════════════════════ Indicators ══════════════════════

    [Fact]
    public async Task Indicators_blank_code_is_rejected()
    {
        var r = await Indicators(new FakeStore()).Handle(new GetIndicatorSeriesQuery { Code = "  " }, default);
        r.IsSuccess.Should().BeFalse();
        r.Error.Should().Contain("code is required");
    }

    [Fact]
    public async Task Indicators_from_after_to_is_rejected()
    {
        var r = await Indicators(new FakeStore()).Handle(
            new GetIndicatorSeriesQuery { Code = "USD_LKR", From = D(2026, 7, 1), To = D(2026, 6, 1) }, default);
        r.IsSuccess.Should().BeFalse();
        r.Error.Should().Contain("from must be on or before to");
    }

    [Fact]
    public async Task Indicators_empty_series_returns_200_empty_list()
    {
        var store = new FakeStore { IndicatorLatest = null };
        var r = await Indicators(store).Handle(new GetIndicatorSeriesQuery { Code = "USD_LKR" }, default);
        r.IsSuccess.Should().BeTrue();
        r.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Indicators_default_window_is_365_days_ending_at_latest()
    {
        var store = new FakeStore { IndicatorLatest = D(2026, 7, 12) };
        await Indicators(store).Handle(new GetIndicatorSeriesQuery { Code = "USD_LKR" }, default);

        store.CapturedIndicatorWindow!.Value.To.Should().Be(D(2026, 7, 12));
        // 365 inclusive days => from = to - 364.
        store.CapturedIndicatorWindow!.Value.From.Should().Be(D(2026, 7, 12).AddDays(-364));
    }

    [Fact]
    public async Task Indicators_explicit_window_passed_through_and_inclusive()
    {
        var store = new FakeStore
        {
            IndicatorRows =
            {
                new IndicatorPointRow(D(2026, 6, 1), "USD_LKR", 300m, "open.er-api.com"),
                new IndicatorPointRow(D(2026, 6, 15), "USD_LKR", 301m, "open.er-api.com"),
                new IndicatorPointRow(D(2026, 7, 1), "USD_LKR", 302m, "open.er-api.com"),
            }
        };
        var r = await Indicators(store).Handle(
            new GetIndicatorSeriesQuery { Code = "USD_LKR", From = D(2026, 6, 1), To = D(2026, 7, 1) }, default);

        store.IndicatorLatest.Should().BeNull("latest must NOT be queried when To is explicit");
        store.CapturedIndicatorWindow!.Value.Should().Be((D(2026, 6, 1), D(2026, 7, 1)));
        r.Data.Should().HaveCount(3);              // both fence-post days included
        r.Data.First().Date.Should().Be("2026-06-01");
        r.Data.First().IndicatorCode.Should().Be("USD_LKR");
        r.Data.First().Value.Should().Be(300m);
    }

    // ══════════════════════ Macro (two-date discipline) ══════════════════════

    [Fact]
    public async Task Macro_blank_key_is_rejected()
    {
        var r = await Macro(new FakeStore()).Handle(new GetMacroSeriesQuery { Key = "" }, default);
        r.IsSuccess.Should().BeFalse();
        r.Error.Should().Contain("key is required");
    }

    [Fact]
    public async Task Macro_from_after_to_is_rejected()
    {
        var r = await Macro(new FakeStore()).Handle(
            new GetMacroSeriesQuery { Key = "CCPI_BASE2021", From = D(2026, 6, 1), To = D(2026, 1, 1) }, default);
        r.IsSuccess.Should().BeFalse();
    }

    [Fact]
    public async Task Macro_empty_series_returns_200_empty_list()
    {
        var r = await Macro(new FakeStore { MacroLatest = null })
            .Handle(new GetMacroSeriesQuery { Key = "CCPI_BASE2021" }, default);
        r.IsSuccess.Should().BeTrue();
        r.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Macro_default_window_anchors_on_referenceDate_latest()
    {
        var store = new FakeStore { MacroLatest = D(2026, 6, 1) };
        await Macro(store).Handle(new GetMacroSeriesQuery { Key = "CCPI_BASE2021" }, default);
        store.CapturedMacroWindow!.Value.To.Should().Be(D(2026, 6, 1));
        store.CapturedMacroWindow!.Value.From.Should().Be(D(2026, 6, 1).AddDays(-364));
    }

    /// <summary>
    /// The leakage tripwire: referenceDate and publishedAt must reach the wire on SEPARATE
    /// fields, verbatim. This test FAILS if a future change maps publishedAt onto referenceDate,
    /// derives one from the other, or drops either.
    /// </summary>
    [Fact]
    public async Task Macro_both_dates_are_returned_verbatim_and_distinct()
    {
        var store = new FakeStore
        {
            MacroRows =
            {
                // referenceDate (period) and publishedAt (vintage, ~30d later) are DIFFERENT.
                new MacroPointRow("CCPI_BASE2021", D(2026, 6, 1), D(2026, 6, 30), 207.7m, "CBSL_CCPI"),
            }
        };
        var r = await Macro(store).Handle(
            new GetMacroSeriesQuery { Key = "CCPI_BASE2021", From = D(2026, 6, 1), To = D(2026, 6, 1) }, default);

        r.IsSuccess.Should().BeTrue();
        var p = r.Data.Single();
        p.SeriesKey.Should().Be("CCPI_BASE2021");
        p.ReferenceDate.Should().Be("2026-06-01");   // the period described
        p.PublishedAt.Should().Be("2026-06-30");     // when it became knowable
        p.ReferenceDate.Should().NotBe(p.PublishedAt, "the two dates must never be collapsed");
        p.Value.Should().Be(207.7m);
    }

    [Fact]
    public async Task Macro_multiple_vintages_of_same_period_all_survive()
    {
        // A revised print: same ReferenceDate, later PublishedAt — both rows must be returned.
        var store = new FakeStore
        {
            MacroRows =
            {
                new MacroPointRow("FOOD_IMPORTS_YOY", D(2026, 4, 1), D(2026, 5, 20), 10m, "CBSL"),
                new MacroPointRow("FOOD_IMPORTS_YOY", D(2026, 4, 1), D(2026, 6, 25), 12m, "CBSL"),
            }
        };
        var r = await Macro(store).Handle(
            new GetMacroSeriesQuery { Key = "FOOD_IMPORTS_YOY", From = D(2026, 4, 1), To = D(2026, 4, 1) }, default);
        r.Data.Should().HaveCount(2);
        r.Data.Select(x => x.PublishedAt).Should().BeEquivalentTo(new[] { "2026-05-20", "2026-06-25" });
    }

    // ══════════════════════ Catalog ══════════════════════

    [Fact]
    public async Task Catalog_empty_db_returns_200_empty_list()
    {
        var r = await CatalogH(new FakeStore()).Handle(new GetSeriesCatalogQuery(), default);
        r.IsSuccess.Should().BeTrue();
        r.Data.Should().BeEmpty();
    }

    [Fact]
    public async Task Catalog_combines_both_kinds_and_orders_deterministically()
    {
        var store = new FakeStore
        {
            Catalog =
            {
                new SeriesCatalogRow("USD_LKR", "indicator", D(2026, 7, 12), 37),
                new SeriesCatalogRow("CCPI_HEADLINE_YOY_BASE2021", "macro", D(2026, 6, 1), 18),
                new SeriesCatalogRow("CCPI_BASE2021", "macro", D(2026, 6, 1), 18),
            }
        };
        var r = await CatalogH(store).Handle(new GetSeriesCatalogQuery(), default);
        r.IsSuccess.Should().BeTrue();
        r.Data.Should().HaveCount(3);
        // "indicator" sorts before "macro"; within a kind, by key.
        r.Data.Select(c => c.Key).Should()
            .ContainInOrder("USD_LKR", "CCPI_BASE2021", "CCPI_HEADLINE_YOY_BASE2021");
        var usd = r.Data[0];
        usd.Kind.Should().Be("indicator");
        usd.LatestDate.Should().Be("2026-07-12");
        usd.Count.Should().Be(37);
    }
}
