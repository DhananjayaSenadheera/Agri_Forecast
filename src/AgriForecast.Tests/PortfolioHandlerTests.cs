using System.Text.Json;
using AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;
using AgriForecast.Application.Requests.Portfolio.Commands.RemoveWatchlistCrop;
using AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistEntry;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Requests.Portfolio.Queries.GetDashboard;
using AgriForecast.Application.Requests.Portfolio.Queries.GetWatchlist;
using AgriForecast.Application.Requests.Portfolio.Validators;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
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
/// The second target is the CAPS and the PUT MATRIX. Markets are now per crop (up to
/// WatchlistLimits.MaxMarketsPerCrop of them, on up to MaxCropsPerUser crops), and PUT distinguishes three
/// states per field — replace, clear, and leave alone — which is exactly the kind of contract that rots
/// silently if only the happy path is tested.
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
    private static readonly Guid Pettah = Guid.Parse("b2a20001-0000-0000-0000-000000000004");
    private static readonly Guid Meegoda = Guid.Parse("b2a20001-0000-0000-0000-000000000008");
    private static readonly Guid UnknownMarket = Guid.Parse("b2a20001-0000-0000-0000-0000000000ff");

    private static readonly DateTime Seeded = new(2026, 7, 20, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Planted = new(2026, 5, 1);

    // Fake watchlist repository + unit of work. Holds rows for EVERY user and filters by the id it is
    // asked for, exactly like the real repository's WHERE clause — so an unscoped handler fails here.
    // Child rows live on the entities themselves (as they do in EF once Included), so the repository's
    // insert/delete calls are RECORDED rather than replayed: what is asserted is that the handler told the
    // repository about exactly the rows the entity produced.
    private sealed class FakeWatchlist : IUserCropWatchlistRepository, IUnitofWorkRepository
    {
        public readonly List<UserCropWatchlist> Rows = new();
        public readonly List<UserCropWatchMarket> AddedMarkets = new();
        public readonly List<UserCropWatchMarket> RemovedMarkets = new();
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

        public Task AddMarketsAsync(
            IEnumerable<UserCropWatchMarket> markets, CancellationToken ct = default)
        {
            AddedMarkets.AddRange(markets);
            return Task.CompletedTask;
        }

        public void RemoveMarkets(IEnumerable<UserCropWatchMarket> markets)
            => RemovedMarkets.AddRange(markets);

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
        public readonly Dictionary<Guid, string> MarketShortCodes = new();
        public Guid? EconomicCentreId;

        // (marketId, cropId) -> observations. Keyed exactly as the real store queries them.
        public readonly List<(Guid MarketId, PortfolioObservationRow Row)> Observations = new();
        public readonly List<PortfolioSnapshotRow> Snapshots = new();

        public readonly List<Guid> CapturedWatchlistUserIds = new();

        // Which markets the price reads were issued for, in order. The dashboard must group crops by
        // market: one anchor call and one window call per DISTINCT market, never per (crop, market).
        public readonly List<Guid> AnchorCallMarkets = new();
        public readonly List<Guid> ObservationCallMarkets = new();

        public void AddCrop(Guid id, string name, string? code = null) => Crops[id] = (name, code);

        public void AddMarket(Guid id, string name, bool isEconomicCentre = false, string shortCode = "MKT")
        {
            Markets[id] = new PortfolioMarketRow(id, name, shortCode, isEconomicCentre);
            MarketShortCodes[id] = shortCode;
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
                    r.PlantedDate,
                    // Oldest-chosen first, exactly like the real store's ORDER BY — the dashboard's
                    // "first watched market" rule depends on this order being deterministic.
                    r.Markets
                        .Select(m => new WatchlistMarketRow(
                            m.MarketId,
                            Markets.TryGetValue(m.MarketId, out var mk) ? mk.Name : "?",
                            MarketShortCodes.TryGetValue(m.MarketId, out var sc) ? sc : string.Empty))
                        .ToList(),
                    r.CreatedAtUtc))
                .OrderBy(r => r.CropName)
                .ThenBy(r => r.CropId)
                .ToList();

            return Task.FromResult<IReadOnlyList<WatchlistRow>>(rows);
        }

        private IEnumerable<PortfolioObservationRow> Usable(
            IReadOnlyCollection<Guid> cropIds, Guid marketId)
            => Observations.Where(o => o.MarketId == marketId && cropIds.Contains(o.Row.CropId))
                .Select(o => o.Row);

        // Per-crop MAX, exactly like the real store's GROUP BY — a crop with data is listed with its OWN
        // freshest date, never with the set's.
        public Task<IReadOnlyList<CropLatestObservation>> GetLatestObservedDatesAsync(
            IReadOnlyCollection<Guid> cropIds, Guid marketId, CancellationToken ct = default)
        {
            AnchorCallMarkets.Add(marketId);
            return Task.FromResult<IReadOnlyList<CropLatestObservation>>(
                Usable(cropIds, marketId)
                    .GroupBy(r => r.CropId)
                    .Select(g => new CropLatestObservation(g.Key, g.Max(r => r.Date)))
                    .ToList());
        }

        // Each crop is filtered by its OWN window, mirroring the real store's per-window queries.
        public Task<IReadOnlyList<PortfolioObservationRow>> GetObservationsAsync(
            IReadOnlyCollection<CropObservationWindow> windows, Guid marketId,
            CancellationToken ct = default)
        {
            ObservationCallMarkets.Add(marketId);
            return Task.FromResult<IReadOnlyList<PortfolioObservationRow>>(
                windows.SelectMany(w =>
                        Usable(new[] { w.CropId }, marketId).Where(r => r.Date >= w.FromInclusive))
                    .ToList());
        }

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

    // A store seeded with the reference data every test needs, plus the markets and three crops.
    private static FakeStore NewStore()
    {
        var store = new FakeStore();
        store.AddCrop(Carrot, "Carrot", "VEG000001");
        store.AddCrop(Tomato, "Tomato", "VEG000002");
        store.AddCrop(Onion, "Onion", "VEG000003");
        store.AddMarket(Dambulla, "Dambulla Dedicated Economic Centre", isEconomicCentre: true, shortCode: "DEC");
        store.AddMarket(Keppetipola, "Keppetipola Dedicated Economic Centre", shortCode: "KEP");
        store.AddMarket(Pettah, "Pettah (HARTI wholesale)", shortCode: "PET");
        store.AddMarket(Meegoda, "Meegoda Dedicated Economic Centre", shortCode: "MEE");
        return store;
    }

    // Seeds one watched crop. Markets are attached in the order given, which is the order the fake store
    // (and the real one) then reports them in.
    private static UserCropWatchlist Seed(
        FakeStore store, Guid userId, Guid cropId, Guid[]? markets = null,
        int dayOffset = 0, DateOnly? plantedDate = null)
    {
        var createdAt = Seeded.AddDays(dayOffset);
        var row = UserCropWatchlist.Create(userId, cropId, plantedDate, createdAt);

        if (markets is { Length: > 0 })
            row.ReplaceMarkets(markets, createdAt);

        store.Watchlist.Rows.Add(row);
        return row;
    }

    private static GetWatchlistQueryHandler WatchlistHandler(FakeStore s) => new(s);

    private static GetPortfolioDashboardQueryHandler DashboardHandler(FakeStore s) => new(s);

    private static AddWatchlistCropCommandHandler AddHandler(FakeStore s) =>
        new(s.Watchlist, s, s.Watchlist, NullLogger<AddWatchlistCropCommandHandler>.Instance);

    private static UpdateWatchlistEntryCommandHandler UpdateHandler(FakeStore s) =>
        new(s.Watchlist, s, s.Watchlist, NullLogger<UpdateWatchlistEntryCommandHandler>.Instance);

    private static RemoveWatchlistCropCommandHandler RemoveHandler(FakeStore s) =>
        new(s.Watchlist, s.Watchlist, NullLogger<RemoveWatchlistCropCommandHandler>.Instance);

    // GET /watchlist.

    [Fact]
    public async Task GetWatchlist_ReturnsOnlyTheCallersCrops()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });
        Seed(store, UserB, Tomato, new[] { Keppetipola });
        Seed(store, UserB, Onion, new[] { Keppetipola });

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
        Seed(store, UserA, Carrot, new[] { Keppetipola, Dambulla }, plantedDate: Planted);

        var result = await WatchlistHandler(store)
            .Handle(new GetWatchlistQuery { UserId = UserA }, default);

        var item = result.Data.Single();
        item.CropName.Should().Be("Carrot");
        item.CropCode.Should().Be("VEG000001");
        item.PlantedDate.Should().Be("2026-05-01",
            "a planting day is a date string, not an instant — shipping it as a DateTime is how it becomes "
            + "the day before for half the world");
        item.Markets.Select(m => m.MarketId).Should().Equal(new[] { Keppetipola, Dambulla },
            "markets keep the order the farmer chose them in");
        item.Markets[0].Name.Should().Be("Keppetipola Dedicated Economic Centre");
        item.Markets[0].ShortCode.Should().Be("KEP");
        item.CreatedAtUtc.Kind.Should().Be(DateTimeKind.Utc,
            "without the Z the UI would read the instant as local time (+5:30 in Sri Lanka)");
    }

    [Fact]
    public async Task GetWatchlist_ACropWithNoMarkets_HasAnEmptyListNotNull()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot);

        var result = await WatchlistHandler(store)
            .Handle(new GetWatchlistQuery { UserId = UserA }, default);

        var item = result.Data.Single();
        item.Markets.Should().NotBeNull().And.BeEmpty(
            "no market chosen is a normal state read as the national default, not missing data");
        item.PlantedDate.Should().BeNull();
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
    public async Task Add_CreatesRow_WithNoMarkets_WhenNoneAreAskedFor()
    {
        var store = NewStore();

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand { UserId = UserA, CropId = Carrot }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.AlreadyPresent.Should().BeFalse();
        result.Data.Item.Markets.Should().BeEmpty(
            "adding a crop without naming a market is legitimate — the dashboard reads it as the "
            + "economic-centre default");
        result.Data.Item.PlantedDate.Should().BeNull("POST does not carry a planting date; PUT sets it");
        store.Watchlist.CommitCount.Should().Be(1);
    }

    [Fact]
    public async Task Add_AttachesTheRequestedMarkets_InOrder()
    {
        var store = NewStore();

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Keppetipola, Dambulla }
            },
            default);

        result.Data.Item.Markets.Select(m => m.MarketId).Should().Equal(Keppetipola, Dambulla);
        store.Watchlist.AddedMarkets.Should().HaveCount(2,
            "the handler tells the repository exactly which child rows to insert");
        store.Watchlist.RemovedMarkets.Should().BeEmpty("an add never deletes");
    }

    [Fact]
    public async Task Add_CollapsesDuplicateMarketIds_RatherThanRejectingThem()
    {
        var store = NewStore();

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Keppetipola, Keppetipola, Dambulla }
            },
            default);

        result.IsSuccess.Should().BeTrue(
            "asking for the same market twice is asking for one market, not an error worth a 4xx");
        result.Data.Item.Markets.Select(m => m.MarketId).Should().Equal(Keppetipola, Dambulla);
    }

    [Fact]
    public async Task Add_WithMoreMarketsThanTheCap_Is422TooManyMarkets_AndWritesNothing()
    {
        var store = NewStore();

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Dambulla, Keppetipola, Pettah, Meegoda }
            },
            default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PortfolioErrors.TooManyMarkets);
        PortfolioErrors.IsUnprocessable(result.Error).Should().BeTrue(
            "a well-formed request the product refuses is a 422; a 400 would send a developer hunting a "
            + "serialization bug that is not there");
        store.Watchlist.Rows.Should().BeEmpty("the crop is not half-added");
        store.Watchlist.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Add_UpToTheCropCap_Succeeds_AndTheNextOneIs422WatchlistFull()
    {
        var store = NewStore();
        for (var i = 0; i < WatchlistLimits.MaxCropsPerUser - 1; i++)
            Seed(store, UserA, Guid.NewGuid(), dayOffset: i);

        // The 10th crop still fits.
        var last = await AddHandler(store).Handle(
            new AddWatchlistCropCommand { UserId = UserA, CropId = Carrot }, default);

        last.IsSuccess.Should().BeTrue();
        store.Watchlist.Rows.Should().HaveCount(WatchlistLimits.MaxCropsPerUser);

        // The 11th does not.
        var overflow = await AddHandler(store).Handle(
            new AddWatchlistCropCommand { UserId = UserA, CropId = Tomato }, default);

        overflow.IsSuccess.Should().BeFalse();
        overflow.Error.Should().Be(PortfolioErrors.WatchlistFull);
        store.Watchlist.Rows.Should().HaveCount(WatchlistLimits.MaxCropsPerUser,
            "the refused add leaves the watchlist exactly as it was");
    }

    [Fact]
    public async Task Add_AtTheCropCap_StillAnswersARepeatAddIdempotently()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, dayOffset: 0);
        for (var i = 1; i < WatchlistLimits.MaxCropsPerUser; i++)
            Seed(store, UserA, Guid.NewGuid(), dayOffset: i);

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand { UserId = UserA, CropId = Carrot }, default);

        result.IsSuccess.Should().BeTrue(
            "the cap is about NEW crops; a farmer sitting on the limit re-tapping a crop they already "
            + "watch has not asked for anything new");
        result.Data.AlreadyPresent.Should().BeTrue();
    }

    [Fact]
    public async Task Add_CapCountsOnlyTheCallersOwnCrops()
    {
        var store = NewStore();
        for (var i = 0; i < WatchlistLimits.MaxCropsPerUser; i++)
            Seed(store, UserB, Guid.NewGuid(), dayOffset: i);

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand { UserId = UserA, CropId = Carrot }, default);

        result.IsSuccess.Should().BeTrue("another farmer's full watchlist is not this farmer's problem");
    }

    [Fact]
    public async Task Add_IsIdempotent_WhenTheCropIsAlreadyWatched()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand { UserId = UserA, CropId = Carrot }, default);

        result.IsSuccess.Should().BeTrue("a double-tap is the user asking for a state they already have");
        result.Data.AlreadyPresent.Should().BeTrue();
        result.Data.Item.CropId.Should().Be(Carrot);
        result.Data.Item.Markets.Should().ContainSingle(
            "a repeat add with no markets must not clear the ones already chosen");
        store.Watchlist.Rows.Should().HaveCount(1, "no duplicate row is created");
    }

    [Fact]
    public async Task Add_OfAnAlreadyWatchedCrop_AddsMarkets_WithoutRemovingTheExistingOnes()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Keppetipola }
            },
            default);

        result.Data.AlreadyPresent.Should().BeTrue();
        result.Data.Item.Markets.Select(m => m.MarketId).Should().Equal(new[] { Dambulla, Keppetipola },
            "POST is insert-only; replacing the set is what PUT is for");
        store.Watchlist.RemovedMarkets.Should().BeEmpty();
    }

    [Fact]
    public async Task Add_ToACropAlreadyAtTheMarketCap_Is422_NotASilentlyDroppedMarket()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla, Keppetipola, Pettah });

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Meegoda }
            },
            default);

        result.IsSuccess.Should().BeFalse(
            "the request carries only ONE market, so counting the request alone would pass it — the cap "
            + "must be measured against what the crop would END UP following");
        result.Error.Should().Be(PortfolioErrors.TooManyMarkets);
        store.Watchlist.Rows.Single().Markets.Select(m => m.MarketId)
            .Should().Equal(new[] { Dambulla, Keppetipola, Pettah });
        store.Watchlist.CommitCount.Should().Be(0,
            "a 200 that silently dropped the market would leave the farmer believing it was added");
    }

    [Fact]
    public async Task Add_ToACropWithRoomForExactlyOneMore_Succeeds()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla, Keppetipola });

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Pettah }
            },
            default);

        result.IsSuccess.Should().BeTrue("landing exactly on the cap is allowed; only exceeding it is not");
        result.Data.Item.Markets.Should().HaveCount(WatchlistLimits.MaxMarketsPerCrop);
    }

    [Fact]
    public async Task Add_ReSendingMarketsTheCropAlreadyFollows_StaysWithinTheCap()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla, Keppetipola, Pettah });

        var result = await AddHandler(store).Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Dambulla, Pettah }
            },
            default);

        result.IsSuccess.Should().BeTrue(
            "the union of existing and requested is still three markets — a double-tap of markets the "
            + "crop already follows asks for nothing new");
        result.Data.AlreadyPresent.Should().BeTrue();
        result.Data.Item.Markets.Should().HaveCount(WatchlistLimits.MaxMarketsPerCrop);
    }

    [Fact]
    public async Task Add_DoesNotTouchAnotherUsersRows()
    {
        var store = NewStore();
        Seed(store, UserB, Tomato, new[] { Dambulla });

        await AddHandler(store).Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserA,
                CropId = Onion,
                MarketIds = new List<Guid> { Keppetipola }
            },
            default);

        store.Watchlist.Rows.Single(r => r.UserId == UserB).Markets.Select(m => m.MarketId)
            .Should().Equal(new[] { Dambulla }, "writes are scoped to the CALLER's rows");
    }

    // PUT /watchlist/{cropId} — the three-state matrix.

    [Fact]
    public async Task Update_WithMarketIds_ReplacesTheWholeSet()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla, Keppetipola });

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Keppetipola, Pettah }
            },
            default);

        result.IsSuccess.Should().BeTrue();
        result.Data.MarketsChanged.Should().BeTrue();
        // ORDER-SENSITIVE on purpose. Keppetipola was already attached, so it keeps its original position;
        // only Pettah is new and it is appended. A replace does NOT reorder, and the order matters because
        // the transitional dashboard prices a crop at markets[0].
        result.Data.Item.Markets.Select(m => m.MarketId)
            .Should().Equal(new[] { Keppetipola, Pettah });
        store.Watchlist.RemovedMarkets.Select(m => m.MarketId).Should().Equal(new[] { Dambulla },
            "a full replace deletes exactly what is no longer wanted");
        store.Watchlist.CommitCount.Should().Be(1, "both halves of the update are one transaction");
    }

    [Fact]
    public async Task Update_WithAnEmptyMarketArray_ClearsThem()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid>()
            },
            default);

        result.Data.Item.Markets.Should().BeEmpty(
            "an empty array is a deliberate 'clear my markets'; omitting the field is how a caller says "
            + "'leave them alone'");
        result.Data.MarketsChanged.Should().BeTrue();
    }

    [Fact]
    public async Task Update_WithoutMarketIds_LeavesTheMarketsAlone()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand { UserId = UserA, CropId = Carrot, PlantedDate = Planted },
            default);

        result.Data.Item.Markets.Select(m => m.MarketId).Should().Equal(Dambulla);
        result.Data.MarketsChanged.Should().BeFalse();
        store.Watchlist.RemovedMarkets.Should().BeEmpty(
            "a planting-date-only update must not silently empty the farmer's market list");
    }

    [Fact]
    public async Task Update_ReplacingWithTheSameMarkets_ReportsNoChange()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Dambulla }
            },
            default);

        result.Data.MarketsChanged.Should().BeFalse();
        store.Watchlist.AddedMarkets.Should().BeEmpty();
        store.Watchlist.RemovedMarkets.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_WithMoreMarketsThanTheCap_Is422_AndChangesNothing()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Dambulla, Keppetipola, Pettah, Meegoda }
            },
            default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PortfolioErrors.TooManyMarkets);
        store.Watchlist.Rows.Single().Markets.Select(m => m.MarketId).Should().Equal(new[] { Dambulla },
            "everything is validated before anything is mutated");
        store.Watchlist.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Update_SetsAndClearsThePlantingDate()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot);

        var set = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand { UserId = UserA, CropId = Carrot, PlantedDate = Planted },
            default);

        set.Data.Item.PlantedDate.Should().Be("2026-05-01");
        set.Data.PlantedDateChanged.Should().BeTrue();

        var cleared = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand { UserId = UserA, CropId = Carrot, PlantedDate = null },
            default);

        cleared.Data.Item.PlantedDate.Should().BeNull(
            "an explicit null clears the date — the farmer un-telling us is a real request");
        cleared.Data.PlantedDateChanged.Should().BeTrue();
    }

    [Fact]
    public async Task Update_WithoutAPlantingDateKey_LeavesItUnchanged()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, plantedDate: Planted);

        // No assignment to PlantedDate at all: this is the "omitted" state, which System.Text.Json
        // produces by never calling the setter.
        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Dambulla }
            },
            default);

        result.Data.Item.PlantedDate.Should().Be("2026-05-01",
            "omitted and null are different requests: a market-only update must not wipe the date");
        result.Data.PlantedDateChanged.Should().BeFalse();
    }

    [Fact]
    public async Task Update_WithTheSamePlantingDate_ReportsNoChange()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, plantedDate: Planted);

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand { UserId = UserA, CropId = Carrot, PlantedDate = Planted },
            default);

        result.Data.PlantedDateChanged.Should().BeFalse();
    }

    [Fact]
    public async Task Update_RejectsAPlantingDateInTheFuture()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot);

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Carrot,
                PlantedDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7)
            },
            default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PortfolioErrors.InvalidPlantedDate);
        store.Watchlist.Rows.Single().PlantedDate.Should().BeNull();
    }

    [Fact]
    public async Task Update_AcceptsTodayAndTomorrow_BecauseTheFarmersDayCanBeAheadOfUtc()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot);

        // Sri Lanka is UTC+5:30, so during their evening the farmer's "today" is already the next UTC
        // date. One day of slack is what stops the honest answer being rejected.
        foreach (var offset in new[] { 0, 1 })
        {
            var result = await UpdateHandler(store).Handle(
                new UpdateWatchlistEntryCommand
                {
                    UserId = UserA,
                    CropId = Carrot,
                    PlantedDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset)
                },
                default);

            result.IsSuccess.Should().BeTrue($"UTC today +{offset} is a plausible local today");
        }

        var twoDaysOut = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Carrot,
                PlantedDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2)
            },
            default);

        twoDaysOut.Error.Should().Be(PortfolioErrors.InvalidPlantedDate,
            "no time zone is two days ahead of UTC — that is a future date, not a local one");
    }

    [Fact]
    public async Task Update_RejectsAPlantingDateBeforeTheFloor()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot);

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Carrot,
                PlantedDate = WatchlistLimits.EarliestPlantedDate.AddDays(-1)
            },
            default);

        result.Error.Should().Be(PortfolioErrors.InvalidPlantedDate,
            "a pre-2000 date is a mis-keyed year, not a memory");
    }

    [Fact]
    public async Task Update_RejectsABadDateBeforeApplyingTheMarkets()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Keppetipola },
                PlantedDate = new DateOnly(1999, 1, 1)
            },
            default);

        result.IsSuccess.Should().BeFalse();
        store.Watchlist.Rows.Single().Markets.Select(m => m.MarketId).Should().Equal(new[] { Dambulla },
            "a request that fails halfway must leave the entry exactly as it was");
        store.Watchlist.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Update_TouchesOnlyTheCropInTheRoute()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla }, dayOffset: 0);
        Seed(store, UserA, Tomato, new[] { Dambulla }, dayOffset: 1);

        await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Keppetipola }
            },
            default);

        store.Watchlist.Rows.Single(r => r.CropId == Tomato).Markets.Select(m => m.MarketId)
            .Should().Equal(new[] { Dambulla },
                "markets are per crop now — the old one-home-market-per-farmer rewrite is gone");
    }

    [Fact]
    public async Task Update_OfAnotherUsersRow_Is404_AndChangesNothing()
    {
        var store = NewStore();
        Seed(store, UserB, Tomato, new[] { Dambulla });

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Tomato,
                MarketIds = new List<Guid> { Keppetipola }
            },
            default);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(PortfolioErrors.WatchlistEntryNotFound);
        PortfolioErrors.IsNotFound(result.Error).Should().BeTrue(
            "404, never 403 — a 403 would confirm that somebody else watches that crop");
        store.Watchlist.Rows.Single().Markets.Select(m => m.MarketId).Should().Equal(Dambulla);
        store.Watchlist.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Update_OfACropNobodyWatches_IsTheSame404()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });

        var result = await UpdateHandler(store).Handle(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Onion,
                MarketIds = new List<Guid> { Keppetipola }
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
        Seed(store, UserA, Carrot, new[] { Dambulla }, dayOffset: 0);
        Seed(store, UserA, Tomato, new[] { Dambulla }, dayOffset: 1);

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
        Seed(store, UserB, Tomato, new[] { Dambulla });

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
        Seed(store, UserA, Carrot, new[] { Dambulla });

        var result = await RemoveHandler(store).Handle(
            new RemoveWatchlistCropCommand { UserId = UserA, CropId = Onion }, default);

        result.Error.Should().Be(PortfolioErrors.WatchlistEntryNotFound);
    }

    // GET /dashboard — the per-market blocks.

    private static PortfolioDashboardItem_GetDto Item(PortfolioDashboard_GetDto dto, Guid cropId)
        => dto.Items.Single(i => i.CropId == cropId);

    private static PortfolioDashboardMarket_GetDto Block(
        PortfolioDashboard_GetDto dto, Guid cropId, Guid marketId)
        => Item(dto, cropId).Markets.Single(m => m.MarketId == marketId);

    [Fact]
    public async Task Dashboard_HasNoTopLevelHomeMarket_AtAll()
    {
        // Structural, not "is it null": the concept is retired, and a field that is always null is a field
        // some future UI will read as meaningful. The serialized body must not carry the key either.
        typeof(PortfolioDashboard_GetDto).GetProperties().Select(p => p.Name)
            .Should().NotContain("HomeMarket");

        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Keppetipola });

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var json = JsonSerializer.Serialize(result.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.Should().NotContain("homeMarket");
    }

    [Fact]
    public async Task Dashboard_EmptyWatchlist_IsAnEmptyItemList()
    {
        var store = NewStore();

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Items.Should().BeEmpty("an empty watchlist is a valid state, not an error");
    }

    // GET /dashboard — isolation.

    [Fact]
    public async Task Dashboard_ReturnsOnlyTheCallersCrops()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });
        Seed(store, UserB, Tomato, new[] { Dambulla });
        Seed(store, UserB, Onion, new[] { Dambulla });

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        result.Data.Items.Should().ContainSingle().Which.CropId.Should().Be(Carrot);
        store.CapturedWatchlistUserIds.Should().AllBeEquivalentTo(UserA);
    }

    // GET /dashboard — which markets a crop gets a block for.

    [Fact]
    public async Task Dashboard_GivesEveryWatchedMarketItsOwnBlock_InTheFarmersOwnOrder()
    {
        var store = NewStore();
        // Chosen in an order that does NOT match the market GUIDs' sort order, so a projection that lost
        // the tick-apart stamping would reorder these and change which tab the UI opens on.
        Seed(store, UserA, Carrot, new[] { Pettah, Dambulla, Keppetipola });

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var markets = Item(result.Data, Carrot).Markets;
        markets.Select(m => m.MarketId).Should().Equal(new[] { Pettah, Dambulla, Keppetipola },
            "markets[0] is the tab the farmer sees first, so the wire order is their first-added order");
        markets.Select(m => m.Name).Should().Equal(new[]
        {
            "Pettah (HARTI wholesale)",
            "Dambulla Dedicated Economic Centre",
            "Keppetipola Dedicated Economic Centre"
        });
        markets.Select(m => m.ShortCode).Should().Equal(new[] { "PET", "DEC", "KEP" });
        markets.Should().OnlyContain(m => !m.IsDefaultMarket,
            "every one of these is the farmer's own choice, not a default standing in");
    }

    [Fact]
    public async Task Dashboard_ACropWithNoMarkets_GetsExactlyOneEconomicCentreBlock_FlaggedAsTheDefault()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 26), min: 300m, max: 320m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var markets = Item(result.Data, Carrot).Markets;
        markets.Should().ContainSingle();
        markets[0].MarketId.Should().Be(Dambulla);
        markets[0].ShortCode.Should().Be("DEC");
        markets[0].IsDefaultMarket.Should().BeTrue(
            "the farmer never picked a market, so this is the national anchor standing in — a default, "
            + "not a failure and not a substitution");
        markets[0].Price!.Price.Should().Be(310m,
            "the card must not be price-empty while the default market has data");
    }

    [Fact]
    public async Task Dashboard_ACropWatchingTheEconomicCentreItself_IsANormalBlock_NotADefault()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        Block(result.Data, Carrot, Dambulla).IsDefaultMarket.Should().BeFalse(
            "choosing Dambulla is a choice; flagging it as a default would tell the farmer the app picked "
            + "it for them");
    }

    [Fact]
    public async Task Dashboard_WithNoEconomicCentreAtAll_LeavesAMarketlessCropHonestlyEmpty()
    {
        // Degenerate database (no market carries the flag). The crop still appears; nothing is invented.
        var store = new FakeStore();
        store.AddCrop(Carrot, "Carrot", "VEG000001");
        Seed(store, UserA, Carrot);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        Item(result.Data, Carrot).Markets.Should().BeEmpty();
    }

    // GET /dashboard — the price inside a block.

    [Fact]
    public async Task Dashboard_ServesEachBlockThePriceOfItsOwnMarket()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Keppetipola, Dambulla });
        store.AddObservation(Keppetipola, Carrot, new DateOnly(2026, 7, 25), min: 180m, max: 200m);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 26), min: 300m, max: 320m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var kep = Block(result.Data, Carrot, Keppetipola).Price!;
        kep.Price.Should().Be(190m, "the midpoint of the day's band, the same rule the market overview uses");
        kep.ObservedDate.Should().Be("2026-07-25");

        var dec = Block(result.Data, Carrot, Dambulla).Price!;
        dec.Price.Should().Be(310m);
        dec.ObservedDate.Should().Be("2026-07-26");
    }

    [Fact]
    public async Task Dashboard_OneCropsTwoMarkets_AreAnchoredIndependently()
    {
        // The per-(crop, market) anchor law. Keppetipola last quoted Carrot in June; Dambulla quoted it
        // today. Neither may pull the other: the stale market keeps reporting its real June price (no
        // staleness cutoff), and the fresh one is unaffected by the stale one.
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Keppetipola, Dambulla });
        store.AddObservation(Keppetipola, Carrot, new DateOnly(2026, 6, 1), min: 180m, max: 200m);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 27), min: 300m, max: 320m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var kep = Block(result.Data, Carrot, Keppetipola);
        kep.Price!.Price.Should().Be(190m, "a price of any age is still a price and is served");
        kep.Price.ObservedDate.Should().Be("2026-06-01");
        kep.PriceUnavailableReason.Should().BeNull();

        var dec = Block(result.Data, Carrot, Dambulla);
        dec.Price!.ObservedDate.Should().Be("2026-07-27",
            "the fresh market is not dragged back by its stale sibling market");
    }

    [Fact]
    public async Task Dashboard_AWatchedMarketWithNoData_IsAnHonestNull_NeverAnotherMarketsPrice()
    {
        // The substitution the redesign removes. Dambulla HAS a Carrot price; Keppetipola does not. The
        // Keppetipola tab must say so rather than quietly show Dambulla's number under Keppetipola's name.
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Keppetipola, Dambulla });
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 26), min: 300m, max: 320m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var kep = Block(result.Data, Carrot, Keppetipola);
        Assert.Null(kep.Price);
        kep.PriceUnavailableReason.Should().Be(PortfolioUnavailableReasons.NoRecentPrice,
            "the farmer chose this market because it is where they can sell; another market's number "
            + "under its name would be a lie about their options");

        Block(result.Data, Carrot, Dambulla).Price!.Price.Should().Be(310m,
            "the market that does have data is unaffected");
    }

    [Fact]
    public async Task Dashboard_NoPriceAtAnyOfACropsMarkets_StillListsThemAll_AndTheCropAppears()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Keppetipola, Pettah });

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var item = result.Data.Items.Should().ContainSingle().Subject;
        item.Markets.Should().HaveCount(2, "a market with nothing to show is still a market they chose");
        item.Markets.Should().OnlyContain(
            m => m.PriceUnavailableReason == PortfolioUnavailableReasons.NoRecentPrice);
        // Assert.Null rather than Should().BeNull(): FluentAssertions 8 binds a bare DTO reference to its
        // enum overload, so the object cast (or this) is needed for a nullable class member.
        Assert.Null(item.Markets[0].Price);
    }

    [Fact]
    public async Task Dashboard_CarriesThePlantingDateOnTheCrop()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla }, plantedDate: Planted);
        Seed(store, UserA, Tomato, new[] { Dambulla }, dayOffset: 1);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        Item(result.Data, Carrot).PlantedDate.Should().Be("2026-05-01");
        Item(result.Data, Tomato).PlantedDate.Should().BeNull(
            "not recorded is a legitimate state, not missing data");
    }

    // GET /dashboard — the trend leg, per (crop, market).

    [Theory]
    [InlineData(180, 200, 200, 220, "up")]
    [InlineData(200, 220, 180, 200, "down")]
    [InlineData(180, 200, 180, 200, "steady")]
    public async Task Dashboard_TrendComparesAgainstTheImmediatelyPreviousObservationAtThatMarket(
        decimal prevMin, decimal prevMax, decimal latestMin, decimal latestMax, string expected)
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 24), min: prevMin, max: prevMax);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: latestMin, max: latestMax);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var price = Block(result.Data, Carrot, Dambulla).Price!;
        price.Direction.Should().Be(expected);
        price.PreviousObservedDate.Should().Be("2026-07-24");
        price.PreviousPrice.Should().Be((prevMin + prevMax) / 2m);
        price.ChangePct.Should().NotBeNull();
    }

    [Fact]
    public async Task Dashboard_TrendIsPerMarket_NotPooledAcrossACropsMarkets()
    {
        // Two markets, opposite movements for the same crop. Pooling the observations would produce one
        // direction for both blocks — and at least one of them would be wrong.
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Keppetipola, Dambulla });
        store.AddObservation(Keppetipola, Carrot, new DateOnly(2026, 7, 24), min: 180m, max: 200m); // 190
        store.AddObservation(Keppetipola, Carrot, new DateOnly(2026, 7, 25), min: 200m, max: 220m); // 210 up
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 24), min: 300m, max: 320m);    // 310
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: 280m, max: 300m);    // 290 down

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        Block(result.Data, Carrot, Keppetipola).Price!.Direction.Should().Be("up");
        Block(result.Data, Carrot, Dambulla).Price!.Direction.Should().Be("down");
    }

    [Fact]
    public async Task Dashboard_SingleObservation_HasANullDirection_NotAFakeSteady()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: 180m, max: 200m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var price = Block(result.Data, Carrot, Dambulla).Price!;
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
        Seed(store, UserA, Carrot, new[] { Dambulla });
        var latest = new DateOnly(2026, 7, 25);
        store.AddObservation(Dambulla, Carrot, latest, min: 180m, max: 200m);
        // One day outside the inclusive TrendWindowDays window ending at the latest observation.
        store.AddObservation(
            Dambulla, Carrot,
            latest.AddDays(-GetPortfolioDashboardQueryHandler.TrendWindowDays),
            min: 100m, max: 120m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var price = Block(result.Data, Carrot, Dambulla).Price!;
        price.Price.Should().Be(190m, "the latest price is still served");
        price.Direction.Should().BeNull(
            "a month-old quote is not what the farmer means by 'versus last time'");
    }

    [Fact]
    public async Task Dashboard_AStaleCropKeepsItsOwnPrice_WhenAFresherCropSharesTheMarket()
    {
        // The sibling-crop independence law, now per market. Anchoring on the MAX date across the crops
        // sharing a market would make Carrot's June price vanish the moment daily-quoted Tomato was added.
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Keppetipola }, dayOffset: 0);
        Seed(store, UserA, Tomato, new[] { Keppetipola }, dayOffset: 1);

        store.AddObservation(Keppetipola, Carrot, new DateOnly(2026, 6, 1), min: 180m, max: 200m);
        store.AddObservation(Keppetipola, Tomato, new DateOnly(2026, 7, 26), min: 100m, max: 120m);
        store.AddObservation(Keppetipola, Tomato, new DateOnly(2026, 7, 27), min: 120m, max: 140m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var carrot = Block(result.Data, Carrot, Keppetipola);
        carrot.Price!.Price.Should().Be(190m, "the crop's own latest price is served however old it is");
        carrot.Price.ObservedDate.Should().Be("2026-06-01");
        carrot.Price.Direction.Should().BeNull("there is no second Carrot observation to compare against");
        carrot.PriceUnavailableReason.Should().BeNull();

        var tomato = Block(result.Data, Tomato, Keppetipola).Price!;
        tomato.ObservedDate.Should().Be("2026-07-27");
        tomato.Direction.Should().Be("up", "the fresher crop is unaffected by the staler one");
    }

    [Fact]
    public async Task Dashboard_TrendWindowIsMeasuredFromEachCropsOwnLatestObservation()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla }, dayOffset: 0);
        Seed(store, UserA, Tomato, new[] { Dambulla }, dayOffset: 1);

        // Carrot's two observations are two days apart but nearly two months behind Tomato's.
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 5, 30), min: 160m, max: 180m);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 6, 1), min: 180m, max: 200m);
        store.AddObservation(Dambulla, Tomato, new DateOnly(2026, 7, 27), min: 100m, max: 120m);

        var price = Block(
            (await DashboardHandler(store).Handle(
                new GetPortfolioDashboardQuery { UserId = UserA }, default)).Data,
            Carrot, Dambulla).Price!;

        price.Direction.Should().Be("up",
            "the 30-day trend window is cut from Carrot's own latest date, not from Tomato's");
        price.PreviousObservedDate.Should().Be("2026-05-30");
        price.ChangePct.Should().Be(11.8m);
    }

    [Fact]
    public async Task Dashboard_AveragesSeveralRowsForTheSameCropMarketAndDay()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });
        // Two sources publishing the same crop/market/day, as HARTI and the DEC scrape both can.
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: 180m, max: 200m); // 190
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: 200m, max: 220m); // 210

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        Block(result.Data, Carrot, Dambulla).Price!.Price.Should().Be(200m);
    }

    [Fact]
    public async Task Dashboard_UsesWholesaleWhenTheBandIsAbsent()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), wholesale: 175m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        Block(result.Data, Carrot, Dambulla).Price!.Price.Should().Be(175m,
            "the shared ObservedUnitPrice precedence, not a portfolio-only rule");
    }

    // GET /dashboard — read batching.

    [Fact]
    public async Task Dashboard_BatchesThePriceReadsByMarket_NotByCropTimesMarket()
    {
        // Three crops across two markets = 6 (crop, market) blocks, but only 2 distinct markets. A
        // per-block read would be 6 anchor calls; the caps (10 crops x 3 markets) exist precisely so this
        // stays bounded, and grouping is what keeps it that way.
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla, Keppetipola }, dayOffset: 0);
        Seed(store, UserA, Tomato, new[] { Dambulla, Keppetipola }, dayOffset: 1);
        Seed(store, UserA, Onion, new[] { Dambulla, Keppetipola }, dayOffset: 2);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: 180m, max: 200m);
        store.AddObservation(Keppetipola, Tomato, new DateOnly(2026, 7, 25), min: 100m, max: 120m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        result.Data.Items.Should().HaveCount(3);
        store.AnchorCallMarkets.Should().HaveCount(2);
        store.AnchorCallMarkets.Should().OnlyHaveUniqueItems("one anchor read per DISTINCT market");
        store.ObservationCallMarkets.Should().OnlyHaveUniqueItems("and one window read per distinct market");
        store.ObservationCallMarkets.Should().HaveCountLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Dashboard_ACropSharingAMarketWithAnother_IsReadInTheSamePass()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla }, dayOffset: 0);
        Seed(store, UserA, Tomato, new[] { Dambulla }, dayOffset: 1);
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: 180m, max: 200m);
        store.AddObservation(Dambulla, Tomato, new DateOnly(2026, 7, 25), min: 100m, max: 120m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        store.AnchorCallMarkets.Should().ContainSingle();
        Block(result.Data, Carrot, Dambulla).Price!.Price.Should().Be(190m);
        Block(result.Data, Tomato, Dambulla).Price!.Price.Should().Be(110m);
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
        Seed(store, UserA, Carrot, new[] { Dambulla });
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
    public async Task Dashboard_PredictionIsNationalAndIgnoresTheWatchedMarkets()
    {
        // Markets are a display choice; the model is not per market. Two crops with different watched
        // markets get the prediction their snapshot holds, unchanged.
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Keppetipola }, dayOffset: 0);
        Seed(store, UserA, Tomato, new[] { Pettah }, dayOffset: 1);
        store.Snapshots.Add(Snapshot(Carrot, new DateOnly(2026, 7, 27), predicted: 204.55m));
        store.Snapshots.Add(Snapshot(Tomato, new DateOnly(2026, 7, 27), predicted: 88.20m));

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        result.Data.Items.Single(i => i.CropId == Carrot).Prediction!.PredictedPrice.Should().Be(204.55m);
        result.Data.Items.Single(i => i.CropId == Tomato).Prediction!.PredictedPrice.Should().Be(88.20m);
    }

    [Fact]
    public async Task Dashboard_NoSnapshot_IsANullLegWithAReason_AndTheCropStillAppears()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });
        store.AddObservation(Dambulla, Carrot, new DateOnly(2026, 7, 25), min: 180m, max: 200m);

        var result = await DashboardHandler(store)
            .Handle(new GetPortfolioDashboardQuery { UserId = UserA }, default);

        var item = result.Data.Items.Single();
        // The price leg is independent of the prediction leg: one market block with a real price, and a
        // null prediction beside it.
        Assert.NotNull(item.Markets.Single().Price);
        Assert.Null(item.Prediction);
        item.PredictionUnavailableReason.Should().Be(PortfolioUnavailableReasons.NoSnapshot);
    }

    [Fact]
    public async Task Dashboard_SnapshotWithNoHarvestDate_RendersWithANullHarvestDate()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new[] { Dambulla });
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
    public async Task AddValidator_RejectsAnUnknownMarket_ButAcceptsOmittedAndEmptyLists()
    {
        var store = NewStore();
        var validator = new AddWatchlistCropCommandValidator(store);

        var bad = await validator.ValidateAsync(new AddWatchlistCropCommand
        {
            UserId = UserA,
            CropId = Carrot,
            MarketIds = new List<Guid> { Dambulla, UnknownMarket }
        });
        bad.IsValid.Should().BeFalse("one bad id in the list fails the request, not just that element");

        var empty = await validator.ValidateAsync(new AddWatchlistCropCommand
        {
            UserId = UserA,
            CropId = Carrot,
            MarketIds = new List<Guid>()
        });
        empty.IsValid.Should().BeTrue();

        var omitted = await validator.ValidateAsync(new AddWatchlistCropCommand
        {
            UserId = UserA,
            CropId = Carrot
        });
        omitted.IsValid.Should().BeTrue("adding a crop with no market is a legitimate request");
    }

    [Fact]
    public async Task AddValidator_DoesNotEnforceTheCaps()
    {
        var store = NewStore();

        var result = await new AddWatchlistCropCommandValidator(store).ValidateAsync(
            new AddWatchlistCropCommand
            {
                UserId = UserA,
                CropId = Carrot,
                MarketIds = new List<Guid> { Dambulla, Keppetipola, Pettah, Meegoda }
            });

        result.IsValid.Should().BeTrue(
            "the caps are 422 wire codes from the handler so the UI can say WHICH limit was hit; a 400 "
            + "here would flatten them into 'bad request'");
    }

    [Fact]
    public async Task UpdateValidator_AcceptsOmittedMarkets_ButRejectsAnUnknownOne()
    {
        var store = NewStore();
        var validator = new UpdateWatchlistEntryCommandValidator(store);

        var omitted = await validator.ValidateAsync(new UpdateWatchlistEntryCommand
        {
            UserId = UserA,
            CropId = Carrot
        });
        omitted.IsValid.Should().BeTrue("omitting marketIds means 'leave them alone'");

        var cleared = await validator.ValidateAsync(new UpdateWatchlistEntryCommand
        {
            UserId = UserA,
            CropId = Carrot,
            MarketIds = new List<Guid>()
        });
        cleared.IsValid.Should().BeTrue("clearing every market is a valid choice");

        var unknown = await validator.ValidateAsync(new UpdateWatchlistEntryCommand
        {
            UserId = UserA,
            CropId = Carrot,
            MarketIds = new List<Guid> { UnknownMarket }
        });
        unknown.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateValidator_DoesNotCheckOwnership_SoAForeignCropStaysA404()
    {
        var store = NewStore();

        var result = await new UpdateWatchlistEntryCommandValidator(store).ValidateAsync(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Tomato, // watched by user B in the handler tests
                MarketIds = new List<Guid> { Dambulla }
            });

        result.IsValid.Should().BeTrue(
            "ownership is answered by the handler as a flat 404; a validator would make it a 400 that "
            + "distinguishes 'no such row' from 'somebody else's row'");
    }

    [Fact]
    public async Task UpdateValidator_LeavesTheDateRangeToTheHandler()
    {
        var store = NewStore();

        var result = await new UpdateWatchlistEntryCommandValidator(store).ValidateAsync(
            new UpdateWatchlistEntryCommand
            {
                UserId = UserA,
                CropId = Carrot,
                PlantedDate = new DateOnly(1900, 1, 1)
            });

        result.IsValid.Should().BeTrue(
            "the range needs a clock, which belongs in a handler — and the answer is the "
            + "invalid_planted_date 422, not a 400");
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

    // The wire codes themselves.

    [Fact]
    public void WireCodes_AreTheFrozenSnakeCaseStrings_TheUiSwitchesOn()
    {
        PortfolioErrors.WatchlistEntryNotFound.Should().Be("watchlist_entry_not_found");
        PortfolioErrors.WatchlistFull.Should().Be("watchlist_full");
        PortfolioErrors.TooManyMarkets.Should().Be("too_many_markets");
        PortfolioErrors.InvalidPlantedDate.Should().Be("invalid_planted_date");

        PortfolioErrors.UnprocessableCodes.Should().BeEquivalentTo(new[]
        {
            "watchlist_full", "too_many_markets", "invalid_planted_date"
        });
        PortfolioErrors.IsNotFound("watchlist_full").Should().BeFalse(
            "the two families map to different status codes and must not overlap");
    }

    [Fact]
    public void Caps_AreTheOwnerDecidedNumbers()
    {
        WatchlistLimits.MaxCropsPerUser.Should().Be(10);
        WatchlistLimits.MaxMarketsPerCrop.Should().Be(3);
        WatchlistLimits.EarliestPlantedDate.Should().Be(new DateOnly(2000, 1, 1));
    }
}
