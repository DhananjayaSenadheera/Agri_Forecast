using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

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

    // Only the one table is created. EnsureCreated is avoided for the same reason the audit tests avoid
    // it: the full model's ISJSON check constraint and SYSUTCDATETIME() defaults are not SQLite.
    private static async Task<(SqliteConnection conn, AgriForecastDbContext ctx)> BuildSqliteAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        var ctx = new AgriForecastDbContext(
            new DbContextOptionsBuilder<AgriForecastDbContext>().UseSqlite(connection).Options);

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
}
