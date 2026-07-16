using AgriForecast.Application.Requests.Market.Quaries.GetAll;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Unit tests for GetMarketsQueryHandler (API-1). The DB is faked via a canned
/// IMarketReadStore so mapping, enum-int fidelity, the hasPrices flag threading, and the
/// empty-result shape are exercised in isolation. NOT covered here: the store's SQL
/// EXISTS filter for hasPrices — no relational test provider is referenced, so it is
/// verified by inspection + a manual live check only. If a store-level harness ever
/// lands, its first test belongs on that filter (held row must not qualify a market).
/// </summary>
public class MarketsQueryHandlerTests
{
    private static readonly Guid M1 = Guid.Parse("aaaa0001-0000-0000-0000-000000000001");
    private static readonly Guid M2 = Guid.Parse("aaaa0002-0000-0000-0000-000000000002");

    private static GetMarketsQueryHandler Build(FakeStore store)
        => new(store, Mock.Of<ILogger<GetMarketsQueryHandler>>());

    private sealed class FakeStore : IMarketReadStore
    {
        public List<MarketListRow> Rows = new();
        public bool? CapturedHasPricesOnly;

        public Task<IReadOnlyList<MarketListRow>> GetMarketsAsync(
            bool hasPricesOnly, CancellationToken ct = default)
        {
            CapturedHasPricesOnly = hasPricesOnly;
            // Model the store's filter so the handler test reflects real behaviour: when
            // hasPricesOnly, only rows flagged as price-carrying survive.
            IReadOnlyList<MarketListRow> rows = hasPricesOnly
                ? Rows.Where(r => PriceCarrying.Contains(r.Id)).ToList()
                : Rows.ToList();
            return Task.FromResult(rows);
        }

        public HashSet<Guid> PriceCarrying = new();
    }

    private static MarketListRow Row(Guid id, string name, string? district, MarketType type, bool eco)
        => new(id, name, district, type, eco);

    // ---- Empty ------------------------------------------------------------

    [Fact]
    public async Task Empty_registry_returns_success_with_empty_list()
    {
        var result = await Build(new FakeStore()).Handle(new GetMarketsQuery(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }

    // ---- Mapping + enum-int fidelity --------------------------------------

    [Fact]
    public async Task Maps_all_fields_including_null_district_and_marketType()
    {
        var store = new FakeStore
        {
            Rows =
            {
                Row(M1, "Dambulla Dedicated Economic Centre", "Matale", MarketType.DEC, true),
                Row(M2, "CBSL National Average", null, MarketType.NationalAggregate, false),
            }
        };

        var result = await Build(store).Handle(new GetMarketsQuery(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(2);

        var dec = result.Data.Single(m => m.Id == M1);
        dec.Name.Should().Be("Dambulla Dedicated Economic Centre");
        dec.District.Should().Be("Matale");
        dec.MarketType.Should().Be(MarketType.DEC);
        // The DTO exposes the enum; the API has no JsonStringEnumConverter so it
        // serializes as the underlying int the FE expects.
        ((int)dec.MarketType).Should().Be(2);
        dec.IsEconomicCenter.Should().BeTrue();

        var agg = result.Data.Single(m => m.Id == M2);
        agg.District.Should().BeNull();
        ((int)agg.MarketType).Should().Be(3);
        agg.IsEconomicCenter.Should().BeFalse();
    }

    // ---- hasPrices flag threading + filter --------------------------------

    [Fact]
    public async Task Default_query_does_not_request_hasPricesOnly()
    {
        var store = new FakeStore();
        await Build(store).Handle(new GetMarketsQuery(), default);
        store.CapturedHasPricesOnly.Should().BeFalse();
    }

    [Fact]
    public async Task HasPrices_true_is_threaded_to_store_and_filters_out_price_less_markets()
    {
        var store = new FakeStore
        {
            Rows =
            {
                Row(M1, "Dambulla", "Matale", MarketType.DEC, true),
                Row(M2, "Empty Market", "Colombo", MarketType.Wholesale, false),
            },
            PriceCarrying = { M1 }, // only M1 carries confirmed prices
        };

        var result = await Build(store).Handle(new GetMarketsQuery { HasPrices = true }, default);

        store.CapturedHasPricesOnly.Should().BeTrue();
        result.Data.Should().ContainSingle().Which.Id.Should().Be(M1);
    }
}
