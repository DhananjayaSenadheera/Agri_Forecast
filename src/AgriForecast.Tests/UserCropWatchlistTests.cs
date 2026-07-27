using AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;
using AgriForecast.Application.Services;
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
/// Guards for the UserCropWatchlist entity, its EF mapping and its repository.
/// <para>
/// Three things are load-bearing here. (1) The factory refuses the ids that would turn into an opaque FK
/// error later — Guid.Empty is never a market, it is an unset client variable. (2) The mapping enforces one
/// row per (user, crop) and the right delete behaviour per FK: an account takes its watchlist with it, but
/// reference data a farmer is watching cannot be deleted out from under them. (3) The repository is
/// user-scoped by construction — there is no way to load a row without naming whose it is.
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
    private static readonly DateTime CreatedUtc = new(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime UpdatedUtc = new(2026, 7, 28, 9, 0, 0, DateTimeKind.Utc);

    // Entity factory.

    [Fact]
    public void Create_MintsRow_WithBothTimestampsAtTheSameInstant()
    {
        var row = UserCropWatchlist.Create(UserId, CropId, MarketId, CreatedUtc);

        row.Id.Should().NotBe(Guid.Empty);
        row.UserId.Should().Be(UserId);
        row.CropId.Should().Be(CropId);
        row.PreferredMarketId.Should().Be(MarketId);
        row.CreatedAtUtc.Should().Be(CreatedUtc);
        row.UpdatedAtUtc.Should().Be(CreatedUtc,
            "a row that has never been changed was last updated when it was created");
    }

    [Fact]
    public void Create_AcceptsNullMarket_AsTheNationalDefault()
    {
        var row = UserCropWatchlist.Create(UserId, CropId, null, CreatedUtc);

        row.PreferredMarketId.Should().BeNull(
            "null is how 'no market chosen' is spelled — the dashboard reads it as the economic-centre default");
    }

    [Fact]
    public void Create_RejectsEmptyUserId()
    {
        var act = () => UserCropWatchlist.Create(Guid.Empty, CropId, MarketId, CreatedUtc);
        act.Should().Throw<ArgumentException>().WithParameterName("userId");
    }

    [Fact]
    public void Create_RejectsEmptyCropId()
    {
        var act = () => UserCropWatchlist.Create(UserId, Guid.Empty, MarketId, CreatedUtc);
        act.Should().Throw<ArgumentException>().WithParameterName("cropId");
    }

    [Fact]
    public void Create_RejectsEmptyGuidMarket_BecauseNullIsHowNoMarketIsSpelled()
    {
        var act = () => UserCropWatchlist.Create(UserId, CropId, Guid.Empty, CreatedUtc);
        act.Should().Throw<ArgumentException>().WithParameterName("preferredMarketId");
    }

    [Fact]
    public void Create_RejectsDefaultCreatedAt()
    {
        var act = () => UserCropWatchlist.Create(UserId, CropId, MarketId, default);
        act.Should().Throw<ArgumentException>().WithParameterName("createdAtUtc");
    }

    // SetPreferredMarket.

    [Fact]
    public void SetPreferredMarket_ChangesValueAndStampsUpdatedAt_AndReportsTheChange()
    {
        var row = UserCropWatchlist.Create(UserId, CropId, MarketId, CreatedUtc);

        var changed = row.SetPreferredMarket(OtherMarketId, UpdatedUtc);

        changed.Should().BeTrue();
        row.PreferredMarketId.Should().Be(OtherMarketId);
        row.UpdatedAtUtc.Should().Be(UpdatedUtc);
        row.CreatedAtUtc.Should().Be(CreatedUtc, "the creation instant is history, not state");
    }

    [Fact]
    public void SetPreferredMarket_ToNull_ClearsBackToTheNationalDefault()
    {
        var row = UserCropWatchlist.Create(UserId, CropId, MarketId, CreatedUtc);

        row.SetPreferredMarket(null, UpdatedUtc).Should().BeTrue();

        row.PreferredMarketId.Should().BeNull();
    }

    [Fact]
    public void SetPreferredMarket_ToTheSameValue_IsANoOp_AndDoesNotChurnUpdatedAt()
    {
        var row = UserCropWatchlist.Create(UserId, CropId, MarketId, CreatedUtc);

        var changed = row.SetPreferredMarket(MarketId, UpdatedUtc);

        changed.Should().BeFalse(
            "the user-wide home-market write touches every row, so rows that already agree must not be re-stamped");
        row.UpdatedAtUtc.Should().Be(CreatedUtc);
    }

    [Fact]
    public void SetPreferredMarket_RejectsEmptyGuid()
    {
        var row = UserCropWatchlist.Create(UserId, CropId, null, CreatedUtc);

        var act = () => row.SetPreferredMarket(Guid.Empty, UpdatedUtc);

        act.Should().Throw<ArgumentException>().WithParameterName("preferredMarketId");
    }

    [Fact]
    public void SetPreferredMarket_RejectsDefaultUpdatedAt()
    {
        var row = UserCropWatchlist.Create(UserId, CropId, null, CreatedUtc);

        var act = () => row.SetPreferredMarket(MarketId, default);

        act.Should().Throw<ArgumentException>().WithParameterName("updatedAtUtc");
    }

    // EF mapping. The model is built against the SQL Server provider (never connected) so the assertions
    // are about the real production mapping, not a test-only approximation.

    private static IEntityType WatchlistEntityType()
    {
        var options = new DbContextOptionsBuilder<AgriForecastDbContext>()
            .UseSqlServer("Server=(model-only);Database=none;")
            .Options;
        using var ctx = new AgriForecastDbContext(options);
        return ctx.Model.FindEntityType(typeof(UserCropWatchlist))!;
    }

    [Fact]
    public void Mapping_UsesTheReservedSingularTableName()
    {
        WatchlistEntityType().GetTableName().Should().Be("UserCropWatchlist",
            "the PRD reserves this exact table name; the DbSet is plural only because a property cannot "
            + "share its name with the entity type");
    }

    [Fact]
    public void Mapping_HasUniqueIndexOnUserAndCrop()
    {
        var index = WatchlistEntityType().GetIndexes()
            .Single(i => i.Name == "UX_UserCropWatchlist_UserCrop");

        index.IsUnique.Should().BeTrue("a farmer watches a crop once, not twice");
        index.Properties.Select(p => p.Name).Should().Equal("UserId", "CropId");
    }

    [Theory]
    // Users CASCADE: deleting an account takes its watchlist with it — an orphan row would be personal
    // data with nobody to scope it to.
    [InlineData("UserId", DeleteBehavior.Cascade)]
    // Crops / Markets RESTRICT: reference data a farmer is actively watching cannot be deleted out from
    // under them; the delete fails loudly instead of silently emptying somebody's watchlist.
    [InlineData("CropId", DeleteBehavior.Restrict)]
    [InlineData("PreferredMarketId", DeleteBehavior.Restrict)]
    public void Mapping_UsesTheIntendedDeleteBehaviourPerForeignKey(string property, DeleteBehavior expected)
    {
        var fk = WatchlistEntityType().GetForeignKeys()
            .Single(f => f.Properties.Single().Name == property);

        fk.DeleteBehavior.Should().Be(expected);
    }

    [Fact]
    public void Mapping_KeepsPreferredMarketNullable_ButUserAndCropRequired()
    {
        var et = WatchlistEntityType();

        et.FindProperty("PreferredMarketId")!.IsNullable.Should().BeTrue(
            "no market chosen is a legitimate state, read as the national default");
        et.FindProperty("UserId")!.IsNullable.Should().BeFalse();
        et.FindProperty("CropId")!.IsNullable.Should().BeFalse();
    }

    // Repository + the unique index, against a real (SQLite) database.

    private const string CreateWatchlistTableSql =
        """
        CREATE TABLE "UserCropWatchlist" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_UserCropWatchlist" PRIMARY KEY,
            "UserId" TEXT NOT NULL,
            "CropId" TEXT NOT NULL,
            "PreferredMarketId" TEXT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX "UX_UserCropWatchlist_UserCrop" ON "UserCropWatchlist" ("UserId", "CropId");
        """;

    private static AgriForecastDbContext NewContext(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<AgriForecastDbContext>().UseSqlite(connection).Options);

    // Only the one table is created. EnsureCreated is avoided for the same reason the audit tests avoid
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
                    w.CropId, "Carrot", "VEG000001", w.PreferredMarketId, null,
                    w.CreatedAtUtc, w.UpdatedAtUtc))
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

        var first = await winner.Handle(
            new AddWatchlistCropCommand { UserId = UserId, CropId = CropId }, default);

        // The loser saw an empty watchlist, so it inserts too — straight into UX_UserCropWatchlist_UserCrop.
        var second = await loser.Handle(
            new AddWatchlistCropCommand
            {
                UserId = UserId,
                CropId = CropId,
                PreferredMarketId = MarketId
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
        stored.Single().PreferredMarketId.Should().Be(MarketId,
            "the losing request's explicit market choice is re-applied after the rollback, not dropped");
    }
}
