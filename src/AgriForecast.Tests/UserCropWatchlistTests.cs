using AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// Guards for the UserCropWatchlist aggregate — the row itself, its UserCropWatchMarket children, their EF
/// mapping and the repository.
/// <para>
/// Four things are load-bearing here. (1) The factory refuses the ids that would turn into an opaque FK
/// error later — Guid.Empty is never a market, it is an unset client variable. (2) The CAPS live in the
/// entity, so no caller can attach a fourth market by taking a different route. (3) The mapping enforces
/// one row per (user, crop) and one row per (crop entry, market), with the right delete behaviour per FK:
/// an account takes its watchlist AND its watched markets with it, but reference data a farmer is watching
/// cannot be deleted out from under them. (4) The repository is user-scoped by construction — there is no
/// way to load a row without naming whose it is.
/// </para>
/// Style mirrors ForecastSnapshotTests.cs (entity guards) and UserActivityAuditTests.cs (SQLite harness).
/// </summary>
public class UserCropWatchlistTests
{
    private static readonly Guid UserId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CropId = Guid.Parse("aaaa1111-2222-3333-4444-555566667777");
    private static readonly Guid OtherCropId = Guid.Parse("bbbb1111-2222-3333-4444-555566667777");
    private static readonly Guid MarketId = Guid.Parse("b2a20001-0000-0000-0000-000000000001");
    private static readonly Guid OtherMarketId = Guid.Parse("b2a20001-0000-0000-0000-000000000002");
    private static readonly Guid ThirdMarketId = Guid.Parse("b2a20001-0000-0000-0000-000000000003");
    private static readonly Guid FourthMarketId = Guid.Parse("b2a20001-0000-0000-0000-000000000004");
    private static readonly DateTime CreatedUtc = new(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime UpdatedUtc = new(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Planted = new(2026, 5, 1);

    private static UserCropWatchlist NewEntry(DateOnly? plantedDate = null)
        => UserCropWatchlist.Create(UserId, CropId, plantedDate, CreatedUtc);

    // Entity factory.

    [Fact]
    public void Create_MintsRow_WithBothTimestampsAtTheSameInstant()
    {
        var row = UserCropWatchlist.Create(UserId, CropId, Planted, CreatedUtc);

        row.Id.Should().NotBe(Guid.Empty);
        row.UserId.Should().Be(UserId);
        row.CropId.Should().Be(CropId);
        row.PlantedDate.Should().Be(Planted);
        row.Markets.Should().BeEmpty("a crop is added with no markets until some are attached");
        row.CreatedAtUtc.Should().Be(CreatedUtc);
        row.UpdatedAtUtc.Should().Be(CreatedUtc,
            "a row that has never been changed was last updated when it was created");
    }

    [Fact]
    public void Create_AcceptsNullPlantedDate_AsNotRecorded()
    {
        NewEntry().PlantedDate.Should().BeNull(
            "not telling us when they planted is a legitimate state, not missing data");
    }

    [Fact]
    public void Create_RejectsEmptyUserId()
    {
        var act = () => UserCropWatchlist.Create(Guid.Empty, CropId, null, CreatedUtc);
        act.Should().Throw<ArgumentException>().WithParameterName("userId");
    }

    [Fact]
    public void Create_RejectsEmptyCropId()
    {
        var act = () => UserCropWatchlist.Create(UserId, Guid.Empty, null, CreatedUtc);
        act.Should().Throw<ArgumentException>().WithParameterName("cropId");
    }

    [Fact]
    public void Create_RejectsDefaultCreatedAt()
    {
        var act = () => UserCropWatchlist.Create(UserId, CropId, null, default);
        act.Should().Throw<ArgumentException>().WithParameterName("createdAtUtc");
    }

    [Fact]
    public void Create_RejectsAPlantingDateBeforeTheFloor()
    {
        var act = () => UserCropWatchlist.Create(
            UserId, CropId, WatchlistLimits.EarliestPlantedDate.AddDays(-1), CreatedUtc);

        act.Should().Throw<ArgumentException>().WithParameterName("plantedDate");
    }

    // SetPlantedDate.

    [Fact]
    public void SetPlantedDate_ChangesValueAndStampsUpdatedAt_AndReportsTheChange()
    {
        var row = NewEntry();

        var changed = row.SetPlantedDate(Planted, UpdatedUtc);

        changed.Should().BeTrue();
        row.PlantedDate.Should().Be(Planted);
        row.UpdatedAtUtc.Should().Be(UpdatedUtc);
        row.CreatedAtUtc.Should().Be(CreatedUtc, "the creation instant is history, not state");
    }

    [Fact]
    public void SetPlantedDate_ToNull_ClearsIt()
    {
        var row = NewEntry(Planted);

        row.SetPlantedDate(null, UpdatedUtc).Should().BeTrue();

        row.PlantedDate.Should().BeNull();
    }

    [Fact]
    public void SetPlantedDate_ToTheSameValue_IsANoOp_AndDoesNotChurnUpdatedAt()
    {
        var row = NewEntry(Planted);

        row.SetPlantedDate(Planted, UpdatedUtc).Should().BeFalse();

        row.UpdatedAtUtc.Should().Be(CreatedUtc);
    }

    [Fact]
    public void SetPlantedDate_RejectsADateBeforeTheFloor()
    {
        var row = NewEntry();

        var act = () => row.SetPlantedDate(WatchlistLimits.EarliestPlantedDate.AddDays(-1), UpdatedUtc);

        act.Should().Throw<ArgumentException>().WithParameterName("plantedDate");
    }

    [Fact]
    public void SetPlantedDate_RejectsDefaultUpdatedAt()
    {
        var row = NewEntry();

        var act = () => row.SetPlantedDate(Planted, default);

        act.Should().Throw<ArgumentException>().WithParameterName("updatedAtUtc");
    }

    // ReplaceMarkets — the PUT path.

    [Fact]
    public void ReplaceMarkets_AttachesTheRequestedMarkets_InTheOrderGiven()
    {
        var row = NewEntry();

        var changes = row.ReplaceMarkets(new[] { MarketId, OtherMarketId }, UpdatedUtc);

        changes.Added.Should().HaveCount(2);
        changes.Removed.Should().BeEmpty();
        row.Markets.Select(m => m.MarketId).Should().Equal(MarketId, OtherMarketId);
        row.Markets.Should().OnlyContain(m => m.UserCropWatchlistId == row.Id);
    }

    [Fact]
    public void ReplaceMarkets_KeepsTheCallersOrder_EvenWhenTheMarketIdsSortTheOtherWay()
    {
        var row = NewEntry();

        // ThirdMarketId sorts AFTER MarketId, so a naive (CreatedAtUtc, MarketId) sort over markets that
        // share one instant would silently swap them — and because the dashboard prices a crop at its
        // FIRST market, that would change which market the farmer is shown.
        row.ReplaceMarkets(new[] { ThirdMarketId, MarketId }, UpdatedUtc);

        row.Markets.Select(m => m.MarketId).Should().Equal(new[] { ThirdMarketId, MarketId });
        row.Markets.Select(m => m.CreatedAtUtc).Should().OnlyHaveUniqueItems(
            "markets attached in one call are stamped one tick apart so the caller's order survives");
    }

    [Fact]
    public void ReplaceMarkets_IsAFullReplace_RemovingWhatIsNotInTheNewSet()
    {
        var row = NewEntry();
        row.ReplaceMarkets(new[] { MarketId, OtherMarketId }, CreatedUtc);

        var changes = row.ReplaceMarkets(new[] { OtherMarketId, ThirdMarketId }, UpdatedUtc);

        changes.Removed.Select(m => m.MarketId).Should().Equal(MarketId);
        changes.Added.Select(m => m.MarketId).Should().Equal(ThirdMarketId);
        row.Markets.Select(m => m.MarketId).Should().BeEquivalentTo(new[] { OtherMarketId, ThirdMarketId });
    }

    [Fact]
    public void ReplaceMarkets_WithAnEmptySet_ClearsThemAll()
    {
        var row = NewEntry();
        row.ReplaceMarkets(new[] { MarketId }, CreatedUtc);

        var changes = row.ReplaceMarkets(Array.Empty<Guid>(), UpdatedUtc);

        changes.Removed.Should().ContainSingle();
        row.Markets.Should().BeEmpty("an empty array is a deliberate 'clear my markets', not a no-op");
    }

    [Fact]
    public void ReplaceMarkets_KeepsExistingRows_SoTheDisplayOrderDoesNotShuffle()
    {
        var row = NewEntry();
        row.ReplaceMarkets(new[] { MarketId }, CreatedUtc);
        var original = row.Markets.Single();

        row.ReplaceMarkets(new[] { MarketId, OtherMarketId }, UpdatedUtc);

        row.Markets.First().Id.Should().Be(original.Id,
            "a market the farmer already had must not be deleted and re-inserted — it would jump to the "
            + "end of the oldest-chosen order for no reason");
        row.Markets.First().CreatedAtUtc.Should().Be(CreatedUtc);
    }

    [Fact]
    public void ReplaceMarkets_CollapsesDuplicates_RatherThanRejectingThem()
    {
        var row = NewEntry();

        var changes = row.ReplaceMarkets(new[] { MarketId, MarketId, OtherMarketId }, UpdatedUtc);

        changes.Added.Should().HaveCount(2);
        row.Markets.Select(m => m.MarketId).Should().Equal(MarketId, OtherMarketId);
    }

    [Fact]
    public void ReplaceMarkets_CountsTheCapAfterCollapsingDuplicates()
    {
        var row = NewEntry();

        // Four ids, three distinct — the farmer asked for three markets, so this is legal.
        var act = () => row.ReplaceMarkets(
            new[] { MarketId, MarketId, OtherMarketId, ThirdMarketId }, UpdatedUtc);

        act.Should().NotThrow();
        row.Markets.Should().HaveCount(WatchlistLimits.MaxMarketsPerCrop);
    }

    [Fact]
    public void ReplaceMarkets_RefusesMoreThanTheCap()
    {
        var row = NewEntry();

        var act = () => row.ReplaceMarkets(
            new[] { MarketId, OtherMarketId, ThirdMarketId, FourthMarketId }, UpdatedUtc);

        act.Should().Throw<ArgumentException>().WithParameterName("marketIds",
            "the cap is enforced in the entity as well as the handler, so no other caller can slip a "
            + "fourth market in by a different route");
        row.Markets.Should().BeEmpty("a refused replace must not leave the entry half-changed");
    }

    [Fact]
    public void ReplaceMarkets_RejectsAnEmptyGuid()
    {
        var row = NewEntry();

        var act = () => row.ReplaceMarkets(new[] { Guid.Empty }, UpdatedUtc);

        act.Should().Throw<ArgumentException>().WithParameterName("marketIds");
    }

    [Fact]
    public void ReplaceMarkets_WithNoActualChange_DoesNotChurnUpdatedAt()
    {
        var row = NewEntry();
        row.ReplaceMarkets(new[] { MarketId }, CreatedUtc);

        var changes = row.ReplaceMarkets(new[] { MarketId }, UpdatedUtc);

        changes.Added.Should().BeEmpty();
        changes.Removed.Should().BeEmpty();
        row.UpdatedAtUtc.Should().Be(CreatedUtc);
    }

    // AddMarkets — the POST / race-recovery path.

    [Fact]
    public void AddMarkets_IsInsertOnly_AndNeverRemovesWhatIsAlreadyThere()
    {
        var row = NewEntry();
        row.ReplaceMarkets(new[] { MarketId }, CreatedUtc);

        var added = row.AddMarkets(new[] { OtherMarketId }, UpdatedUtc);

        added.Select(m => m.MarketId).Should().Equal(OtherMarketId);
        row.Markets.Select(m => m.MarketId).Should().Equal(new[] { MarketId, OtherMarketId },
            "the recovery path must not delete the winning request's markets — one tap of a button "
            + "silently undoing the other");
    }

    [Fact]
    public void AddMarkets_SkipsMarketsAlreadyAttached()
    {
        var row = NewEntry();
        row.ReplaceMarkets(new[] { MarketId }, CreatedUtc);

        var added = row.AddMarkets(new[] { MarketId }, UpdatedUtc);

        added.Should().BeEmpty();
        row.Markets.Should().ContainSingle();
        row.UpdatedAtUtc.Should().Be(CreatedUtc);
    }

    [Fact]
    public void AddMarkets_TruncatesAtTheCap_RatherThanThrowing()
    {
        var row = NewEntry();
        row.ReplaceMarkets(new[] { MarketId, OtherMarketId }, CreatedUtc);

        var added = row.AddMarkets(new[] { ThirdMarketId, FourthMarketId }, UpdatedUtc);

        added.Select(m => m.MarketId).Should().Equal(ThirdMarketId);
        row.Markets.Should().HaveCount(WatchlistLimits.MaxMarketsPerCrop,
            "on the idempotent add path the caller already has their crop; failing the whole request over "
            + "a cap reached through a race would be worse than honouring the first three");
    }

    // EF mapping. The model is built against the SQL Server provider (never connected) so the assertions
    // are about the real production mapping, not a test-only approximation.

    private static IEntityType EntityTypeFor(Type clrType)
    {
        var options = new DbContextOptionsBuilder<AgriForecastDbContext>()
            .UseSqlServer("Server=(model-only);Database=none;")
            .Options;
        using var ctx = new AgriForecastDbContext(options);
        return ctx.Model.FindEntityType(clrType)!;
    }

    private static IEntityType WatchlistEntityType() => EntityTypeFor(typeof(UserCropWatchlist));

    private static IEntityType WatchMarketEntityType() => EntityTypeFor(typeof(UserCropWatchMarket));

    [Fact]
    public void Mapping_UsesTheReservedSingularTableName()
    {
        WatchlistEntityType().GetTableName().Should().Be("UserCropWatchlist",
            "the PRD reserves this exact table name; the DbSet is plural only because a property cannot "
            + "share its name with the entity type");
    }

    [Fact]
    public void Mapping_ChildTableIsUserCropWatchMarkets()
    {
        WatchMarketEntityType().GetTableName().Should().Be("UserCropWatchMarkets");
    }

    [Fact]
    public void Mapping_HasUniqueIndexOnUserAndCrop()
    {
        var index = WatchlistEntityType().GetIndexes()
            .Single(i => i.Name == "UX_UserCropWatchlist_UserCrop");

        index.IsUnique.Should().BeTrue("a farmer watches a crop once, not twice");
        index.Properties.Select(p => p.Name).Should().Equal("UserId", "CropId");
    }

    [Fact]
    public void Mapping_HasUniqueIndexOnEntryAndMarket()
    {
        var index = WatchMarketEntityType().GetIndexes()
            .Single(i => i.Name == "UX_UserCropWatchMarkets_EntryMarket");

        index.IsUnique.Should().BeTrue("the same market twice for one crop is a data error");
        index.Properties.Select(p => p.Name).Should().Equal("UserCropWatchlistId", "MarketId");
    }

    [Theory]
    // Users CASCADE: deleting an account takes its watchlist with it — an orphan row would be personal
    // data with nobody to scope it to.
    [InlineData("UserId", DeleteBehavior.Cascade)]
    // Crops RESTRICT: reference data a farmer is actively watching cannot be deleted out from under them;
    // the delete fails loudly instead of silently emptying somebody's watchlist.
    [InlineData("CropId", DeleteBehavior.Restrict)]
    public void Mapping_UsesTheIntendedDeleteBehaviourPerForeignKey(string property, DeleteBehavior expected)
    {
        var fk = WatchlistEntityType().GetForeignKeys()
            .Single(f => f.Properties.Single().Name == property);

        fk.DeleteBehavior.Should().Be(expected);
    }

    [Theory]
    // The parent CASCADEs into its children, so a deleted account (Users -> watchlist -> markets) takes
    // both hops with it and removing a crop takes its markets with it.
    [InlineData("UserCropWatchlistId", DeleteBehavior.Cascade)]
    // A market a farmer is watching cannot be deleted out from under them.
    [InlineData("MarketId", DeleteBehavior.Restrict)]
    public void Mapping_ChildForeignKeysUseTheIntendedDeleteBehaviour(
        string property, DeleteBehavior expected)
    {
        var fk = WatchMarketEntityType().GetForeignKeys()
            .Single(f => f.Properties.Single().Name == property);

        fk.DeleteBehavior.Should().Be(expected);
    }

    [Fact]
    public void Mapping_GivesCropMarketAndUserNoNavigationIntoPersonalData()
    {
        // The leakage posture: HasOne<X>().WithMany() with no inverse. If Market ever gained a collection
        // of watch rows, a careless Include on a reference-data query would drag personal data with it.
        foreach (var fk in WatchlistEntityType().GetForeignKeys()
                     .Concat(WatchMarketEntityType().GetForeignKeys()))
        {
            if (fk.PrincipalEntityType.ClrType == typeof(UserCropWatchlist))
                continue; // the aggregate's own parent -> child navigation, which is intended

            // Assert.Null rather than Should().BeNull(): FluentAssertions 8 binds a nullable-annotated
            // reference like INavigation? to its enum overload (CS0453).
            Assert.True(
                fk.PrincipalToDependent is null,
                $"{fk.PrincipalEntityType.ClrType.Name} must gain no navigation into watchlist data");
        }
    }

    [Fact]
    public void Mapping_StoresPlantedDateAsADateWithNoHiddenTime()
    {
        var property = WatchlistEntityType().FindProperty("PlantedDate")!;

        property.IsNullable.Should().BeTrue("not recording a planting date is a legitimate state");
        property.GetColumnType().Should().Be("date",
            "a planting day has no time component; a hidden 00:00:00 makes 'today' timezone-dependent");
    }

    [Fact]
    public void Mapping_KeepsUserAndCropRequired()
    {
        var et = WatchlistEntityType();

        et.FindProperty("UserId")!.IsNullable.Should().BeFalse();
        et.FindProperty("CropId")!.IsNullable.Should().BeFalse();
        WatchMarketEntityType().FindProperty("MarketId")!.IsNullable.Should().BeFalse(
            "there is no 'null market' child row — a crop with no market simply has no rows");
    }

    // Repository + the indexes, against a real (SQLite) database.

    private const string CreateWatchlistTableSql =
        """
        CREATE TABLE "UserCropWatchlist" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_UserCropWatchlist" PRIMARY KEY,
            "UserId" TEXT NOT NULL,
            "CropId" TEXT NOT NULL,
            "PlantedDate" TEXT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX "UX_UserCropWatchlist_UserCrop" ON "UserCropWatchlist" ("UserId", "CropId");
        CREATE TABLE "UserCropWatchMarkets" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_UserCropWatchMarkets" PRIMARY KEY,
            "UserCropWatchlistId" TEXT NOT NULL,
            "MarketId" TEXT NOT NULL,
            "CreatedAtUtc" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX "UX_UserCropWatchMarkets_EntryMarket"
            ON "UserCropWatchMarkets" ("UserCropWatchlistId", "MarketId");
        """;

    private static AgriForecastDbContext NewContext(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<AgriForecastDbContext>().UseSqlite(connection).Options);

    // Only the two tables are created. EnsureCreated is avoided for the same reason the audit tests avoid
    // it: the full model's ISJSON check constraint and SYSUTCDATETIME() defaults are not SQLite.
    private static async Task<(SqliteConnection conn, AgriForecastDbContext ctx)> BuildSqliteAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var ctx = NewContext(connection);

        await ctx.Database.ExecuteSqlRawAsync(CreateWatchlistTableSql);
        return (connection, ctx);
    }

    [Fact]
    public async Task UniqueIndex_RejectsTheSameCropTwiceForOneUser()
    {
        var (conn, ctx) = await BuildSqliteAsync();
        await using var _c = conn;
        await using var _x = ctx;

        var repo = new UserCropWatchlistRepository(ctx);
        IUnitofWorkRepository uow = new UnitOfWorkRepository(ctx);

        await repo.AddAsync(UserCropWatchlist.Create(UserId, CropId, null, CreatedUtc));
        await uow.CommitAsync();

        await repo.AddAsync(UserCropWatchlist.Create(UserId, CropId, null, UpdatedUtc));

        var act = async () => await uow.CommitAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task UniqueIndex_AllowsTheSameCropForDifferentUsers()
    {
        var (conn, ctx) = await BuildSqliteAsync();
        await using var _c = conn;
        await using var _x = ctx;

        var repo = new UserCropWatchlistRepository(ctx);
        IUnitofWorkRepository uow = new UnitOfWorkRepository(ctx);

        await repo.AddAsync(UserCropWatchlist.Create(UserId, CropId, null, CreatedUtc));
        await repo.AddAsync(UserCropWatchlist.Create(OtherUserId, CropId, null, CreatedUtc));

        var act = async () => await uow.CommitAsync();

        await act.Should().NotThrowAsync(
            "two farmers watching the same crop is the normal case; the key is (user, crop)");
    }

    [Fact]
    public async Task UniqueIndex_RejectsTheSameMarketTwiceForOneWatchedCrop()
    {
        var (conn, ctx) = await BuildSqliteAsync();
        await using var _c = conn;
        await using var _x = ctx;

        var repo = new UserCropWatchlistRepository(ctx);
        IUnitofWorkRepository uow = new UnitOfWorkRepository(ctx);

        var entry = UserCropWatchlist.Create(UserId, CropId, null, CreatedUtc);
        await repo.AddAsync(entry);
        await uow.CommitAsync();

        // Two child rows naming the same market, built directly rather than through the entity (which
        // collapses duplicates) — this is what the DB index has to stop if anything ever bypasses it.
        await repo.AddMarketsAsync(new[]
        {
            UserCropWatchMarket.Create(entry.Id, MarketId, CreatedUtc),
            UserCropWatchMarket.Create(entry.Id, MarketId, UpdatedUtc)
        });

        var act = async () => await uow.CommitAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Repository_LoadsTheWatchedMarketsWithTheirEntry()
    {
        var (conn, ctx) = await BuildSqliteAsync();
        await using var _c = conn;
        await using var _x = ctx;

        var repo = new UserCropWatchlistRepository(ctx);
        IUnitofWorkRepository uow = new UnitOfWorkRepository(ctx);

        var entry = UserCropWatchlist.Create(UserId, CropId, null, CreatedUtc);
        await repo.AddAsync(entry);
        await repo.AddMarketsAsync(entry.ReplaceMarkets(new[] { MarketId, OtherMarketId }, CreatedUtc).Added);
        await uow.CommitAsync();

        // A fresh context, so this is a real load and not the tracked graph the writes left behind.
        await using var readCtx = NewContext(conn);
        var loaded = await new UserCropWatchlistRepository(readCtx).GetAllForUserAsync(UserId);

        loaded.Single().Markets.Select(m => m.MarketId)
            .Should().BeEquivalentTo(new[] { MarketId, OtherMarketId },
                "the caps and the full-replace diff are computed against these children — an entry loaded "
                + "without them would read as 'no markets' and re-insert rows that already exist");
    }

    [Fact]
    public async Task RemovingAWatchedCrop_TakesItsMarketsWithIt()
    {
        var (conn, ctx) = await BuildSqliteAsync();
        await using var _c = conn;
        await using var _x = ctx;

        var repo = new UserCropWatchlistRepository(ctx);
        IUnitofWorkRepository uow = new UnitOfWorkRepository(ctx);

        var entry = UserCropWatchlist.Create(UserId, CropId, null, CreatedUtc);
        await repo.AddAsync(entry);
        await repo.AddMarketsAsync(entry.ReplaceMarkets(new[] { MarketId }, CreatedUtc).Added);
        await uow.CommitAsync();

        var tracked = (await repo.GetAllForUserAsync(UserId)).Single();
        repo.Remove(tracked);
        await uow.CommitAsync();

        await using var verifyCtx = NewContext(conn);
        (await verifyCtx.UserCropWatchMarkets.CountAsync()).Should().Be(0,
            "the child rows cascade with their parent; an orphan would be personal data with nothing to "
            + "scope it to. The same cascade one hop up (Users -> watchlist) is pinned by the mapping "
            + "test above — SQLite here has no Users table to delete from.");
    }

    [Fact]
    public async Task Repository_ReturnsOnlyTheNamedUsersRows()
    {
        var (conn, ctx) = await BuildSqliteAsync();
        await using var _c = conn;
        await using var _x = ctx;

        var repo = new UserCropWatchlistRepository(ctx);
        IUnitofWorkRepository uow = new UnitOfWorkRepository(ctx);

        await repo.AddAsync(UserCropWatchlist.Create(UserId, CropId, null, CreatedUtc));
        await repo.AddAsync(UserCropWatchlist.Create(OtherUserId, CropId, null, CreatedUtc));
        await repo.AddAsync(UserCropWatchlist.Create(OtherUserId, OtherCropId, null, CreatedUtc));
        await uow.CommitAsync();

        var mine = await repo.GetAllForUserAsync(UserId);

        mine.Should().ContainSingle().Which.CropId.Should().Be(CropId);
        mine.Should().OnlyContain(r => r.UserId == UserId,
            "the user filter is baked into the query — there is no by-id load that could reach another farmer's row");
    }

    // The concurrent double-tap. The add handler's "is it already there?" check is a read-then-write, so
    // two in-flight POSTs for the same crop can both decide to insert; the loser hits the unique index.
    // Reproduced against a REAL database (two contexts on one SQLite connection, as two requests have two
    // scoped contexts) because the whole point is the provider's exception, which a fake cannot raise.

    // Models the read-then-write window precisely: the first read is answered from the snapshot the losing
    // request would have taken before the winner committed (an empty watchlist), every read after that
    // tells the truth. Everything else goes straight to the real repository.
    private sealed class StaleFirstReadRepository : IUserCropWatchlistRepository
    {
        private readonly IUserCropWatchlistRepository _inner;
        private bool _snapshotServed;

        public StaleFirstReadRepository(IUserCropWatchlistRepository inner) => _inner = inner;

        public Task<List<UserCropWatchlist>> GetAllForUserAsync(Guid userId, CancellationToken ct = default)
        {
            if (_snapshotServed) return _inner.GetAllForUserAsync(userId, ct);

            _snapshotServed = true;
            return Task.FromResult(new List<UserCropWatchlist>());
        }

        public Task AddAsync(UserCropWatchlist entity, CancellationToken ct = default)
            => _inner.AddAsync(entity, ct);

        public void Remove(UserCropWatchlist entity) => _inner.Remove(entity);

        public Task AddMarketsAsync(
            IEnumerable<UserCropWatchMarket> markets, CancellationToken ct = default)
            => _inner.AddMarketsAsync(markets, ct);

        public void RemoveMarkets(IEnumerable<UserCropWatchMarket> markets) => _inner.RemoveMarkets(markets);
    }

    // The handler's post-commit read-back, served from the same SQLite database. Only the watchlist read is
    // on the add path; anything else being called from here would be a bug worth failing loudly for.
    private sealed class WatchlistOnlyReadStore : IPortfolioReadStore
    {
        private readonly AgriForecastDbContext _db;

        public WatchlistOnlyReadStore(AgriForecastDbContext db) => _db = db;

        public async Task<IReadOnlyList<WatchlistRow>> GetWatchlistAsync(
            Guid userId, CancellationToken ct = default)
            => await _db.UserCropWatchlists.AsNoTracking()
                .Where(w => w.UserId == userId)
                .Select(w => new WatchlistRow(
                    w.CropId, "Carrot", "VEG000001", w.PlantedDate,
                    _db.UserCropWatchMarkets
                        .Where(m => m.UserCropWatchlistId == w.Id)
                        .OrderBy(m => m.CreatedAtUtc)
                        .ThenBy(m => m.MarketId)
                        .Select(m => new WatchlistMarketRow(m.MarketId, "Market", "MKT"))
                        .ToList(),
                    w.CreatedAtUtc))
                .ToListAsync(ct);

        public Task<IReadOnlyList<CropLatestObservation>> GetLatestObservedDatesAsync(
            IReadOnlyCollection<Guid> cropIds, Guid marketId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PortfolioObservationRow>> GetObservationsAsync(
            IReadOnlyCollection<CropObservationWindow> windows, Guid marketId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PortfolioSnapshotRow>> GetLatestSnapshotsAsync(
            IReadOnlyCollection<Guid> cropIds, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PortfolioMarketRow?> GetMarketAsync(Guid marketId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PortfolioMarketRow?> GetEconomicCentreMarketAsync(CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> CropExistsAsync(Guid cropId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    // Records only the levels, which is all the race test needs: a Warning proves the recovery path ran and
    // that the test is still reproducing a real collision rather than quietly degrading into the ordinary
    // sequential double-tap.
    private sealed class LevelRecordingLogger<T> : ILogger<T>
    {
        public readonly List<LogLevel> Levels = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    private static AddWatchlistCropCommandHandler AddHandler(
        AgriForecastDbContext ctx, IUserCropWatchlistRepository repo,
        ILogger<AddWatchlistCropCommandHandler>? logger = null)
        => new(repo, new WatchlistOnlyReadStore(ctx), new UnitOfWorkRepository(ctx),
            logger ?? NullLogger<AddWatchlistCropCommandHandler>.Instance);

    [Fact]
    public async Task Add_LosingAConcurrentInsertRace_IsAnswered200Idempotently_NotA500()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _c = connection;

        await using var winnerCtx = NewContext(connection);
        await using var loserCtx = NewContext(connection);
        await winnerCtx.Database.ExecuteSqlRawAsync(CreateWatchlistTableSql);

        var loserLog = new LevelRecordingLogger<AddWatchlistCropCommandHandler>();
        var winner = AddHandler(winnerCtx, new UserCropWatchlistRepository(winnerCtx));
        var loser = AddHandler(
            loserCtx, new StaleFirstReadRepository(new UserCropWatchlistRepository(loserCtx)), loserLog);

        // The winner adds the crop with one market; the loser asks for a second one.
        var first = await winner.Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserId,
                CropId = CropId,
                MarketIds = new List<Guid> { MarketId }
            },
            default);

        // The loser saw an empty watchlist, so it inserts too — straight into UX_UserCropWatchlist_UserCrop.
        var second = await loser.Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserId,
                CropId = CropId,
                MarketIds = new List<Guid> { OtherMarketId }
            },
            default);

        first.IsSuccess.Should().BeTrue();
        first.Data.AlreadyPresent.Should().BeFalse();

        second.IsSuccess.Should().BeTrue(
            "the caller reached the state they asked for; a 500 for a double-tapped button would be a "
            + "self-inflicted error report");
        second.Data.AlreadyPresent.Should().BeTrue();
        second.Data.Item.CropId.Should().Be(CropId);
        loserLog.Levels.Should().Contain(LogLevel.Warning,
            "the collision really happened and was recovered from — without this the test could pass on "
            + "the ordinary sequential path and prove nothing");

        await using var verifyCtx = NewContext(connection);
        var stored = await verifyCtx.UserCropWatchlists.AsNoTracking()
            .Where(w => w.UserId == UserId).ToListAsync();

        stored.Should().ContainSingle("the unique index still holds — the race must not duplicate the row");

        var markets = await verifyCtx.UserCropWatchMarkets.AsNoTracking()
            .Where(m => m.UserCropWatchlistId == stored.Single().Id)
            .Select(m => m.MarketId)
            .ToListAsync();

        markets.Should().BeEquivalentTo(new[] { MarketId, OtherMarketId },
            "the recovery path re-applies the loser's markets INSERT-ONLY on top of the winner's — a full "
            + "replace there would have deleted the winner's market");
    }
}
