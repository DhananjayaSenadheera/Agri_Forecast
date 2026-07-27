using AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;
using AgriForecast.Application.Requests.Portfolio.Commands.RemoveWatchlistCrop;
using AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistMarket;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.Queries.GetDashboard;
using AgriForecast.Application.Requests.Portfolio.Queries.GetWatchlist;
using AgriForecast.Application.Requests.Portfolio.Validators;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// Unit tests for the farmer-portfolio handlers and validators. The database is faked (a user-scoped
/// watchlist store and a canned read store), so what is covered here is everything that decides what a
/// farmer actually sees or is allowed to touch.
/// <para>
/// CROSS-USER ISOLATION IS THE FIRST-CLASS TARGET. The fakes hold rows for TWO users and honour the user
/// filter exactly as the real repository and read store do, so a handler that forgot to scope would show
/// up here as another farmer's crop in the response — not as a passing test against a single-user fixture.
/// A foreign row must answer 404, never 403: a 403 would confirm the row exists for somebody else.
/// </para>
/// <para>
/// The second target is the HOME-MARKET INVARIANT: one market per farmer, so any write that sets it
/// rewrites every row the farmer owns, and a newly added crop inherits the value already in force.
/// </para>
/// Style mirrors ForecastAccuracyHandlerTests.cs.
/// </summary>
public class PortfolioHandlerTests
{
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid Carrot = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Tomato = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid Onion = Guid.Parse("c0000000-0000-0000-0000-000000000003");

    private static readonly Guid Dambulla = Guid.Parse("b2a20001-0000-0000-0000-000000000001");
    private static readonly Guid Keppetipola = Guid.Parse("b2a20001-0000-0000-0000-000000000002");
    private static readonly Guid UnknownMarket = Guid.Parse("b2a20001-0000-0000-0000-0000000000ff");

    private static readonly DateTime Seeded = new(2026, 7, 20, 6, 0, 0, DateTimeKind.Utc);

    // Fake watchlist repository + unit of work. Holds rows for EVERY user and filters by the id it is
    // asked for, exactly like the real repository's WHERE clause — so an unscoped handler fails here.
    private sealed class FakeWatchlist : IUserCropWatchlistRepository, IUnitofWorkRepository
    {
        public readonly List<UserCropWatchlist> Rows = new();
        public int CommitCount { get; private set; }

        public Task<List<UserCropWatchlist>> GetAllForUserAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(Rows.Where(r => r.UserId == userId)
                .OrderBy(r => r.CreatedAtUtc).ThenBy(r => r.CropId).ToList());

        public Task AddAsync(UserCropWatchlist entity, CancellationToken ct = default)
        {
            Rows.Add(entity);
            return Task.CompletedTask;
        }

        public void Remove(UserCropWatchlist entity) => Rows.Remove(entity);

        public Task CommitAsync()
        {
            CommitCount++;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    // Fake read store. The watchlist projection is derived from the SAME row list the repository holds, so
    // a write made by a handler is visible to the read-back without a second fixture to keep in step.
    private sealed class FakeStore : IPortfolioReadStore
    {
        public FakeWatchlist Watchlist = new();

        public readonly Dictionary<Guid, (string Name, string? Code)> Crops = new();
        public readonly Dictionary<Guid, PortfolioMarketRow> Markets = new();
        public Guid? EconomicCentreId;

        // (marketId, cropId) -> observations. Keyed exactly as the real store queries them.
        public readonly List<(Guid MarketId, PortfolioObservationRow Row)> Observations = new();
        public readonly List<PortfolioSnapshotRow> Snapshots = new();

        public readonly List<Guid> CapturedWatchlistUserIds = new();

        public void AddCrop(Guid id, string name, string? code = null) => Crops[id] = (name, code);

        public void AddMarket(Guid id, string name, bool isEconomicCentre = false)
        {
            Markets[id] = new PortfolioMarketRow(id, name, isEconomicCentre);
            if (isEconomicCentre) EconomicCentreId = id;
        }

        public void AddObservation(
            Guid marketId, Guid cropId, DateOnly date,
            decimal min = 0m, decimal max = 0m, decimal wholesale = 0m, decimal retail = 0m)
            => Observations.Add((marketId,
                new PortfolioObservationRow(cropId, date, min, max, wholesale, retail)));

        public Task<IReadOnlyList<WatchlistRow>> GetWatchlistAsync(
            Guid userId, CancellationToken ct = default)
        {
            CapturedWatchlistUserIds.Add(userId);

            var rows = Watchlist.Rows
                .Where(r => r.UserId == userId)
                .Select(r => new WatchlistRow(
                    r.CropId,
                    Crops.TryGetValue(r.CropId, out var c) ? c.Name : "?",
                    Crops.TryGetValue(r.CropId, out var c2) ? c2.Code : null,
                    r.PreferredMarketId,
                    r.PreferredMarketId.HasValue && Markets.TryGetValue(r.PreferredMarketId.Value, out var m)
                        ? m.Name
                        : null,
                    r.CreatedAtUtc,
                    r.UpdatedAtUtc))
                .OrderBy(r => r.CropName)
                .ThenBy(r => r.CropId)
                .ToList();

            return Task.FromResult<IReadOnlyList<WatchlistRow>>(rows);
        }

        private IEnumerable<PortfolioObservationRow> Usable(
            IReadOnlyCollection<Guid> cropIds, Guid marketId)
            => Observations.Where(o => o.MarketId == marketId && cropIds.Contains(o.Row.CropId))
                .Select(o => o.Row);

        public Task<DateOnly?> GetLatestObservedDateAsync(
            IReadOnlyCollection<Guid> cropIds, Guid marketId, CancellationToken ct = default)
        {
            var dates = Usable(cropIds, marketId).Select(r => r.Date).ToList();
            return Task.FromResult(dates.Count == 0 ? (DateOnly?)null : dates.Max());
        }

        public Task<IReadOnlyList<PortfolioObservationRow>> GetObservationsAsync(
            IReadOnlyCollection<Guid> cropIds, Guid marketId, DateOnly fromInclusive,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PortfolioObservationRow>>(
                Usable(cropIds, marketId).Where(r => r.Date >= fromInclusive).ToList());

        public Task<IReadOnlyList<PortfolioSnapshotRow>> GetLatestSnapshotsAsync(
            IReadOnlyCollection<Guid> cropIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PortfolioSnapshotRow>>(
                Snapshots.Where(s => cropIds.Contains(s.CropId))
                    .GroupBy(s => s.CropId)
                    .Select(g => g.OrderByDescending(s => s.SnapshotDate).First())
                    .ToList());

        public Task<PortfolioMarketRow?> GetMarketAsync(Guid marketId, CancellationToken ct = default)
            => Task.FromResult(Markets.TryGetValue(marketId, out var m) ? m : null);

        public Task<PortfolioMarketRow?> GetEconomicCentreMarketAsync(CancellationToken ct = default)
            => Task.FromResult(EconomicCentreId.HasValue ? Markets[EconomicCentreId.Value] : null);

        public Task<bool> CropExistsAsync(Guid cropId, CancellationToken ct = default)
            => Task.FromResult(Crops.ContainsKey(cropId));
    }

    // A store seeded with the reference data every test needs, plus the two markets and three crops.
    private static FakeStore NewStore()
    {
        var store = new FakeStore();
        store.AddCrop(Carrot, "Carrot", "VEG000001");
        store.AddCrop(Tomato, "Tomato", "VEG000002");
        store.AddCrop(Onion, "Onion", "VEG000003");
        store.AddMarket(Dambulla, "Dambulla Dedicated Economic Centre", isEconomicCentre: true);
        store.AddMarket(Keppetipola, "Keppetipola Dedicated Economic Centre");
        return store;
    }

    private static void Seed(FakeStore store, Guid userId, Guid cropId, Guid? marketId, int dayOffset = 0)
        => store.Watchlist.Rows.Add(
            UserCropWatchlist.Create(userId, cropId, marketId, Seeded.AddDays(dayOffset)));

    private static GetWatchlistQueryHandler WatchlistHandler(FakeStore s) => new(s);

    private static GetPortfolioDashboardQueryHandler DashboardHandler(FakeStore s) => new(s);

    private static AddWatchlistCropCommandHandler AddHandler(FakeStore s) =>
        new(s.Watchlist, s, s.Watchlist, NullLogger<AddWatchlistCropCommandHandler>.Instance);

    private static UpdateWatchlistMarketCommandHandler UpdateHandler(FakeStore s) =>
        new(s.Watchlist, s, s.Watchlist, NullLogger<UpdateWatchlistMarketCommandHandler>.Instance);

    private static RemoveWatchlistCropCommandHandler RemoveHandler(FakeStore s) =>
        new(s.Watchlist, s.Watchlist, NullLogger<RemoveWatchlistCropCommandHandler>.Instance);

    // GET /watchlist.

    [Fact]
    public async Task GetWatchlist_ReturnsOnlyTheCallersCrops()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);
        Seed(store, UserB, Tomato, Keppetipola);
        Seed(store, UserB, Onion, Keppetipola);

        var result = await WatchlistHandler(store)
            .Handle(new GetWatchlistQuery { UserId = UserA }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().ContainSingle().Which.CropId.Should().Be(Carrot);
        result.Data.Should().NotContain(i => i.CropId == Tomato || i.CropId == Onion,
            "user B's crops are never visible to user A");
        store.CapturedWatchlistUserIds.Should().AllBeEquivalentTo(UserA,
            "the read is scoped by the caller's id, not filtered afterwards");
    }

    [Fact]
    public async Task GetWatchlist_MapsDisplayFields_AndStampsCreatedAtAsUtc()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);

        var result = await WatchlistHandler(store)
            .Handle(new GetWatchlistQuery { UserId = UserA }, default);

        var item = result.Data.Single();
        item.CropName.Should().Be("Carrot");
        item.CropCode.Should().Be("VEG000001");
        item.PreferredMarketId.Should().Be(Dambulla);
        item.PreferredMarketName.Should().Be("Dambulla Dedicated Economic Centre");
        item.CreatedAtUtc.Kind.Should().Be(DateTimeKind.Utc,
            "without the Z the UI would read the instant as local time (+5:30 in Sri Lanka)");
    }

    [Fact]
    public async Task GetWatchlist_EmptyWatchlist_IsAnEmptyListNotAFailure()
    {
        var store = NewStore();

        var result = await WatchlistHandler(store)
            .Handle(new GetWatchlistQuery { UserId = UserA }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }

    // POST /watchlist.

    [Fact]
    public async Task Add_CreatesRow_AndInheritsTheUsersCurrentHomeMarket()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Keppetipola);

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand { UserId = UserA, CropId = Tomato }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.AlreadyPresent.Should().BeFalse();
        result.Data.Item.PreferredMarketId.Should().Be(Keppetipola,
            "a new crop joins the farmer's existing home market — adding a crop never resets it");
        store.Watchlist.Rows.Should().HaveCount(2);
        store.Watchlist.CommitCount.Should().Be(1);
    }

    [Fact]
    public async Task Add_WithNoExistingRows_AndNoMarket_LeavesTheMarketNull()
    {
        var store = NewStore();

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand { UserId = UserA, CropId = Carrot }, default);

        result.Data.Item.PreferredMarketId.Should().BeNull(
            "null means 'not chosen', which the dashboard resolves to the economic-centre default");
    }

    [Fact]
    public async Task Add_WithAnExplicitMarket_AppliesItToEveryCropTheUserWatches()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla, dayOffset: 0);
        Seed(store, UserA, Onion, Dambulla, dayOffset: 1);

        await AddHandler(store).Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserA,
                CropId = Tomato,
                PreferredMarketId = Keppetipola
            },
            default);

        store.Watchlist.Rows.Where(r => r.UserId == UserA)
            .Should().OnlyContain(r => r.PreferredMarketId == Keppetipola,
                "one home market per farmer — setting it on the new crop moves all of them");
    }

    [Fact]
    public async Task Add_DoesNotTouchAnotherUsersMarket()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);
        Seed(store, UserB, Tomato, Dambulla);

        await AddHandler(store).Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserA,
                CropId = Onion,
                PreferredMarketId = Keppetipola
            },
            default);

        store.Watchlist.Rows.Single(r => r.UserId == UserB).PreferredMarketId
            .Should().Be(Dambulla, "the user-wide update is scoped to the CALLER's rows");
    }

    [Fact]
    public async Task Add_IsIdempotent_WhenTheCropIsAlreadyWatched()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand { UserId = UserA, CropId = Carrot }, default);

        result.IsSuccess.Should().BeTrue("a double-tap is the user asking for a state they already have");
        result.Data.AlreadyPresent.Should().BeTrue();
        result.Data.Item.CropId.Should().Be(Carrot);
        store.Watchlist.Rows.Should().HaveCount(1, "no duplicate row is created");
    }

    [Fact]
    public async Task Add_OfAnAlreadyWatchedCrop_StillAppliesAnExplicitMarketUserWide()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla, dayOffset: 0);
        Seed(store, UserA, Tomato, Dambulla, dayOffset: 1);

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserA,
                CropId = Carrot,
                PreferredMarketId = Keppetipola
            },
            default);

        result.Data.AlreadyPresent.Should().BeTrue();
        store.Watchlist.Rows.Should().OnlyContain(r => r.PreferredMarketId == Keppetipola);
    }

    // PUT /watchlist/{cropId}.

    [Fact]
    public async Task Update_SetsTheMarketOnEveryCropTheUserWatches()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla, dayOffset: 0);
        Seed(store, UserA, Tomato, Dambulla, dayOffset: 1);
        Seed(store, UserA, Onion, Dambulla, dayOffset: 2);

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistMarketCommand
            {
                UserId = UserA,
                CropId = Carrot,
                PreferredMarketId = Keppetipola
            },
            default);

        result.IsSuccess.Should().BeTrue();
        result.Data.AppliedToCropCount.Should().Be(3);
        result.Data.PreferredMarketName.Should().Be("Keppetipola Dedicated Economic Centre");
        store.Watchlist.Rows.Should().OnlyContain(r => r.PreferredMarketId == Keppetipola);
        store.Watchlist.CommitCount.Should().Be(1, "the whole user-wide change is one transaction");
    }

    [Fact]
    public async Task Update_ToNull_ClearsTheHomeMarketBackToTheNationalDefault()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Keppetipola);

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistMarketCommand { UserId = UserA, CropId = Carrot, PreferredMarketId = null },
            default);

        result.IsSuccess.Should().BeTrue();
        result.Data.PreferredMarketId.Should().BeNull();
        result.Data.PreferredMarketName.Should().BeNull();
        store.Watchlist.Rows.Single().PreferredMarketId.Should().BeNull(
            "unlike POST, an explicit null on PUT is the farmer choosing the national default");
    }

    [Fact]
    public async Task Update_ReportsAppliedCount_EvenWhenTheMarketWasAlreadyInForce()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Keppetipola, dayOffset: 0);
        Seed(store, UserA, Tomato, Keppetipola, dayOffset: 1);

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistMarketCommand
            {
                UserId = UserA,
                CropId = Carrot,
                PreferredMarketId = Keppetipola
            },
            default);

        result.Data.AppliedToCropCount.Should().Be(2,
            "the count is how many crops the market covers, not how many rows happened to change — "
            + "a zero there would read as a failure");
    }

    [Fact]
    public async Task Update_OfAnotherUsersRow_Is404_AndChangesNothing()
    {
        var store = NewStore();
        Seed(store, UserB, Tomato, Dambulla);

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistMarketCommand
            {
                UserId = UserA,
                CropId = Tomato,
                PreferredMarketId = Keppetipola
            },
            default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PortfolioErrors.WatchlistEntryNotFound);
        PortfolioErrors.IsNotFound(result.Error).Should().BeTrue(
            "404, never 403 — a 403 would confirm that somebody else watches that crop");
        store.Watchlist.Rows.Single().PreferredMarketId.Should().Be(Dambulla);
        store.Watchlist.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Update_OfACropNobodyWatches_IsTheSame404()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistMarketCommand
            {
                UserId = UserA,
                CropId = Onion,
                PreferredMarketId = Keppetipola
            },
            default);

        result.Error.Should().Be(PortfolioErrors.WatchlistEntryNotFound,
            "an unwatched crop and another farmer's crop are indistinguishable from outside, by design");
    }

    // DELETE /watchlist/{cropId}.

    [Fact]
    public async Task Remove_DeletesTheCallersRow()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla, dayOffset: 0);
        Seed(store, UserA, Tomato, Dambulla, dayOffset: 1);

        var result = await RemoveHandler(store).Handle(
            new RemoveWatchlistCropCommand { UserId = UserA, CropId = Carrot }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Removed.Should().BeTrue();
        result.Data.CropId.Should().Be(Carrot);
        store.Watchlist.Rows.Should().ContainSingle().Which.CropId.Should().Be(Tomato);
    }

    [Fact]
    public async Task Remove_OfAnotherUsersRow_Is404_AndLeavesItAlone()
    {
        var store = NewStore();
        Seed(store, UserB, Tomato, Dambulla);

        var result = await RemoveHandler(store).Handle(
            new RemoveWatchlistCropCommand { UserId = UserA, CropId = Tomato }, default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PortfolioErrors.WatchlistEntryNotFound);
        store.Watchlist.Rows.Should().ContainSingle(
            "user A must not be able to delete user B's watchlist row");
        store.Watchlist.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Remove_OfAnUnwatchedCrop_Is404()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);

        var result = await RemoveHandler(store).Handle(
            new RemoveWatchlistCropCommand { UserId = UserA, CropId = Onion }, default);

        result.Error.Should().Be(PortfolioErrors.WatchlistEntryNotFound);
    }

    // GET /dashboard — home market.

    [Fact]
    public async Task Dashboard_WithNoChosenMarket_DefaultsToTheEconomicCentre()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, null);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var home = result.Data.HomeMarket!;
        home.MarketId.Should().Be(Dambulla);
        home.IsEconomicCenter.Should().BeTrue();
        home.IsDefault.Should().BeTrue("the farmer has not chosen; this is the default standing in");
    }

    [Fact]
    public async Task Dashboard_WithAChosenMarket_ReportsItAsNonDefaultAndNonEconomicCentre()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Keppetipola);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var home = result.Data.HomeMarket!;
        home.MarketId.Should().Be(Keppetipola);
        home.IsDefault.Should().BeFalse();
        home.IsEconomicCenter.Should().BeFalse(
            "the UI needs this to label the prediction 'National forecast' under a non-centre market");
    }

    [Fact]
    public async Task Dashboard_EmptyWatchlist_StillNamesTheDefaultHomeMarket()
    {
        var store = NewStore();

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Items.Should().BeEmpty();
        result.Data.HomeMarket!.MarketId.Should().Be(Dambulla,
            "the empty state still tells the farmer which market prices would be shown for");
    }

    // GET /dashboard — isolation.

    [Fact]
    public async Task Dashboard_ReturnsOnlyTheCallersCrops()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);
        Seed(store, UserB, Tomato, Dambulla);
        Seed(store, UserB, Onion, Dambulla);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        result.Data.Items.Should().ContainSingle().Which.CropId.Should().Be(Carrot);
        store.CapturedWatchlistUserIds.Should().AllBeEquivalentTo(UserA);
    }

    // GET /dashboard — price leg.

    [Fact]
    public async Task Dashboard_ServesThePriceFromTheHomeMarket_WhenItHasData()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Keppetipola);
        store.AddObservation(Keppetipola, Carrot, new DateOnly(2026, 7, 25), min: 180m, max: 200m);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 26), min: 300m, max: 320m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var price = result.Data.Items.Single().Price!;
        price.MarketId.Should().Be(Keppetipola);
        price.IsFallbackMarket.Should().BeFalse();
        price.Price.Should().Be(190m, "the midpoint of the day's band, the same rule the market overview uses");
        price.ObservedDate.Should().Be("2026-07-25");
    }

    [Fact]
    public async Task Dashboard_FallsBackToTheEconomicCentre_AndFlagsIt()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Keppetipola);
        // Nothing at Keppetipola for this crop; Dambulla has it.
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 26), min: 300m, max: 320m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var price = result.Data.Items.Single().Price!;
        price.MarketId.Should().Be(Dambulla);
        price.MarketName.Should().Be("Dambulla Dedicated Economic Centre");
        price.IsFallbackMarket.Should().BeTrue(
            "showing another market's price as the farmer's own, unlabelled, would be dishonest");
    }

    [Fact]
    public async Task Dashboard_MixedCrops_FallBackIndividually()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Keppetipola, dayOffset: 0);
        Seed(store, UserA, Tomato, Keppetipola, dayOffset: 1);
        store.AddObservation(Keppetipola, Carrot, new DateOnly(2026, 7, 25), min: 180m, max: 200m);
        store.AddObservation(Dambulla, Tomato, new DateOnly(2026, 7, 25), min: 100m, max: 120m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        result.Data.Items.Single(i => i.CropId == Carrot).Price!.IsFallbackMarket.Should().BeFalse();
        result.Data.Items.Single(i => i.CropId == Tomato).Price!.IsFallbackMarket.Should().BeTrue();
    }

    [Fact]
    public async Task Dashboard_NoPriceAnywhere_IsANullLegWithAReason_AndTheCropStillAppears()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Keppetipola);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var item = result.Data.Items.Should().ContainSingle().Subject;
        // Assert.Null rather than Should().BeNull(): FluentAssertions 8 binds a bare DTO reference to its
        // enum overload, so the object cast (or this) is needed for a nullable class member.
        Assert.Null(item.Price);
        item.PriceUnavailableReason.Should().Be(PortfolioUnavailableReasons.NoRecentPrice,
            "a crop the farmer added always appears; a missing price is stated, never invented");
    }

    [Theory]
    [InlineData(180, 200, 200, 220, "up")]
    [InlineData(200, 220, 180, 200, "down")]
    [InlineData(180, 200, 180, 200, "steady")]
    public async Task Dashboard_TrendComparesAgainstTheImmediatelyPreviousObservation(
        decimal prevMin, decimal prevMax, decimal latestMin, decimal latestMax, string expected)
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 24), min: prevMin, max: prevMax);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: latestMin, max: latestMax);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var price = result.Data.Items.Single().Price!;
        price.Direction.Should().Be(expected);
        price.PreviousObservedDate.Should().Be("2026-07-24");
        price.PreviousPrice.Should().Be((prevMin + prevMax) / 2m);
        price.ChangePct.Should().NotBeNull();
    }

    [Fact]
    public async Task Dashboard_SingleObservation_HasANullDirection_NotAFakeSteady()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: 180m, max: 200m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var price = result.Data.Items.Single().Price!;
        price.Price.Should().Be(190m);
        price.Direction.Should().BeNull("there is nothing to compare against; 'steady' would be a claim");
        price.ChangePct.Should().BeNull();
        price.PreviousPrice.Should().BeNull();
        price.PreviousObservedDate.Should().BeNull();
    }

    [Fact]
    public async Task Dashboard_PreviousObservationOlderThanTheTrendWindow_YieldsNoDirection()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);
        var latest = new DateOnly(2026, 7, 25);
        store.AddObservation(Dambulla, Carrot, latest, min: 180m, max: 200m);
        // One day outside the inclusive TrendWindowDays window ending at the latest observation.
        store.AddObservation(
            Dambulla, Carrot,
            latest.AddDays(-GetPortfolioDashboardQueryHandler.TrendWindowDays),
            min: 100m, max: 120m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var price = result.Data.Items.Single().Price!;
        price.Price.Should().Be(190m, "the latest price is still served");
        price.Direction.Should().BeNull(
            "a month-old quote is not what the farmer means by 'versus last time'");
    }

    [Fact]
    public async Task Dashboard_AveragesSeveralRowsForTheSameCropAndDay()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);
        // Two sources publishing the same crop/market/day, as HARTI and the DEC scrape both can.
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: 180m, max: 200m); // 190
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: 200m, max: 220m); // 210

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        result.Data.Items.Single().Price!.Price.Should().Be(200m);
    }

    [Fact]
    public async Task Dashboard_UsesWholesaleWhenTheBandIsAbsent()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), wholesale: 175m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        result.Data.Items.Single().Price!.Price.Should().Be(175m,
            "the shared ObservedUnitPrice precedence, not a portfolio-only rule");
    }

    // GET /dashboard — prediction leg.

    private static PortfolioSnapshotRow Snapshot(
        Guid cropId, DateOnly date, decimal predicted = 204.55m,
        string confidence = "Low", string predictor = "crop_mean_fallback")
        => new(cropId, date, date.AddDays(100), predicted, 150.10m, 260.90m,
            confidence, predictor, "v17");

    [Fact]
    public async Task Dashboard_ServesTheNewestSnapshot_Verbatim()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);
        store.Snapshots.Add(Snapshot(Carrot, new DateOnly(2026, 7, 20), predicted: 100m));
        store.Snapshots.Add(Snapshot(Carrot, new DateOnly(2026, 7, 27), predicted: 204.55m));

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var prediction = result.Data.Items.Single().Prediction!;
        prediction.PredictedPrice.Should().Be(204.55m);
        prediction.LowerBound.Should().Be(150.10m);
        prediction.UpperBound.Should().Be(260.90m);
        prediction.Confidence.Should().Be("Low",
            "a Low-confidence fallback is passed straight through, never upgraded to look confident");
        prediction.ActivePredictor.Should().Be("crop_mean_fallback");
        prediction.ModelVersion.Should().Be("v17");
        prediction.SnapshotDate.Should().Be("2026-07-27");
        prediction.HarvestDate.Should().Be("2026-11-04");
    }

    [Fact]
    public async Task Dashboard_NoSnapshot_IsANullLegWithAReason_AndTheCropStillAppears()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: 180m, max: 200m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var item = result.Data.Items.Single();
        Assert.NotNull(item.Price); // the price leg is independent of the prediction leg
        Assert.Null(item.Prediction);
        item.PredictionUnavailableReason.Should().Be(PortfolioUnavailableReasons.NoSnapshot);
    }

    [Fact]
    public async Task Dashboard_SnapshotWithNoHarvestDate_RendersWithANullHarvestDate()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, Dambulla);
        // A not_maturable row: the growth period could not be resolved, so there is no harvest date. It is
        // still a real served prediction and must still render.
        store.Snapshots.Add(new PortfolioSnapshotRow(
            Carrot, new DateOnly(2026, 7, 27), null, 204.55m, 150.10m, 260.90m,
            "Low", "crop_mean_fallback", "v17"));

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var prediction = result.Data.Items.Single().Prediction!;
        prediction.HarvestDate.Should().BeNull();
        prediction.PredictedPrice.Should().Be(204.55m);
    }

    [Fact]
    public void SnapshotProjection_CarriesNoActualOrErrorColumns()
    {
        // The farmer dashboard shows what the model SAID, never how a past forecast scored — that is the
        // admin accuracy surface. Enforced on the read-store contract itself so no future handler can pick
        // an error column up by accident.
        var forbidden = new[]
        {
            "ActualPrice", "ActualObservedDate", "SignedError", "AbsoluteError",
            "PercentageError", "WithinInterval", "MaturityState", "MaturedAtUtc"
        };

        typeof(PortfolioSnapshotRow).GetProperties().Select(p => p.Name)
            .Should().NotIntersectWith(forbidden);
    }

    // Validators.

    [Fact]
    public async Task AddValidator_RejectsAnUnknownCrop()
    {
        var store = NewStore();
        var result = await new AddWatchlistCropCommandValidator(store).ValidateAsync(
            new AddWatchlistCropCommand { UserId = UserA, CropId = Guid.NewGuid() });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == nameof(AddWatchlistCropCommand.CropId));
    }

    [Fact]
    public async Task AddValidator_RejectsAnEmptyCropId()
    {
        var store = NewStore();
        var result = await new AddWatchlistCropCommandValidator(store).ValidateAsync(
            new AddWatchlistCropCommand { UserId = UserA, CropId = Guid.Empty });

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage == "cropId is required.");
    }

    [Fact]
    public async Task AddValidator_RejectsAnUnknownMarket_ButAcceptsAnOmittedOne()
    {
        var store = NewStore();
        var validator = new AddWatchlistCropCommandValidator(store);

        var bad = await validator.ValidateAsync(new AddWatchlistCropCommand
        {
            UserId = UserA,
            CropId = Carrot,
            PreferredMarketId = UnknownMarket
        });
        bad.IsValid.Should().BeFalse();
        bad.Errors.Should().Contain(e =>
            e.PropertyName == nameof(AddWatchlistCropCommand.PreferredMarketId),
            "the error is keyed under the name the caller sent, not PreferredMarketId.Value");

        var omitted = await validator.ValidateAsync(new AddWatchlistCropCommand
        {
            UserId = UserA,
            CropId = Carrot
        });
        omitted.IsValid.Should().BeTrue("omitted means inherit, which is not a value to validate");
    }

    [Fact]
    public async Task UpdateValidator_AcceptsANullMarket_ButRejectsAnUnknownOne()
    {
        var store = NewStore();
        var validator = new UpdateWatchlistMarketCommandValidator(store);

        var cleared = await validator.ValidateAsync(new UpdateWatchlistMarketCommand
        {
            UserId = UserA,
            CropId = Carrot,
            PreferredMarketId = null
        });
        cleared.IsValid.Should().BeTrue("clearing to the national default is a valid choice on PUT");

        var unknown = await validator.ValidateAsync(new UpdateWatchlistMarketCommand
        {
            UserId = UserA,
            CropId = Carrot,
            PreferredMarketId = UnknownMarket
        });
        unknown.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateValidator_DoesNotCheckOwnership_SoAForeignCropStaysA404()
    {
        var store = NewStore();

        var result = await new UpdateWatchlistMarketCommandValidator(store).ValidateAsync(
            new UpdateWatchlistMarketCommand
            {
                UserId = UserA,
                CropId = Tomato, // watched by user B in the handler tests
                PreferredMarketId = Dambulla
            });

        result.IsValid.Should().BeTrue(
            "ownership is answered by the handler as a flat 404; a validator would make it a 400 that "
            + "distinguishes 'no such row' from 'somebody else's row'");
    }

    [Fact]
    public async Task RemoveValidator_ChecksShapeOnly()
    {
        var validator = new RemoveWatchlistCropCommandValidator();

        (await validator.ValidateAsync(new RemoveWatchlistCropCommand
        {
            UserId = UserA,
            CropId = Guid.NewGuid() // a crop that does not exist at all
        })).IsValid.Should().BeTrue("an unknown crop is a 404 from the handler, not a 400");

        (await validator.ValidateAsync(new RemoveWatchlistCropCommand
        {
            UserId = UserA,
            CropId = Guid.Empty
        })).IsValid.Should().BeFalse();
    }

    // Home-market resolution.

    [Fact]
    public void HomeMarket_Resolve_ReturnsTheNewestWrite_WhenRowsSomehowDisagree()
    {
        var candidates = new[]
        {
            new HomeMarketCandidate(Carrot, Dambulla, new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc)),
            new HomeMarketCandidate(Tomato, Keppetipola, new DateTime(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc))
        };

        HomeMarket.Resolve(candidates).Should().Be(Keppetipola,
            "the rule is total by design: a crash between two writes must not leave the dashboard undefined");
    }

    [Fact]
    public void HomeMarket_Resolve_OnAnEmptyWatchlist_IsNull()
        => HomeMarket.Resolve(Array.Empty<HomeMarketCandidate>()).Should().BeNull();
}
