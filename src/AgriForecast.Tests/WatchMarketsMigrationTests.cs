using AgriForecast.Infrastructure.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace AgriForecast.Tests;

/// <summary>
/// Guards for the data migration that turns each farmer's single home market into a per-crop watched
/// market: <c>PerCropWatchMarketsAndPlantedDate</c>.
/// <para>
/// THE ORDER OF OPERATIONS IS THE WHOLE TEST. The scaffolder emits the DropColumn FIRST, and a
/// copy-then-drop that has been re-ordered back to drop-then-copy loses every market every farmer had
/// chosen — silently, with a green build, because the resulting table is perfectly well-formed and simply
/// empty. These assertions fail the moment the copy stops preceding the drop.
/// </para>
/// <para>
/// WHAT THIS CANNOT DO: execute the migration. The copy is T-SQL (NEWID(), bracketed identifiers) against
/// SQL Server, so the SQLite harness the rest of the suite uses cannot run it. It is asserted structurally
/// here and was verified end-to-end against the dev database, where three watchlist rows — two with a
/// market, one without — produced exactly two child rows carrying the parents' own CreatedAtUtc.
/// </para>
/// </summary>
public class WatchMarketsMigrationTests
{
    private static IReadOnlyList<MigrationOperation> UpOperations()
        => new PerCropWatchMarketsAndPlantedDate().UpOperations;

    private static IReadOnlyList<MigrationOperation> DownOperations()
        => new PerCropWatchMarketsAndPlantedDate().DownOperations;

    private static int IndexOf(IReadOnlyList<MigrationOperation> ops, Func<MigrationOperation, bool> match)
    {
        for (var i = 0; i < ops.Count; i++)
            if (match(ops[i]))
                return i;

        return -1;
    }

    [Fact]
    public void Up_CarriesTheHomeMarketCopy()
    {
        var copies = UpOperations()
            .OfType<SqlOperation>()
            .Where(op => op.Sql == PerCropWatchMarketsAndPlantedDate.CopyPreferredMarketsSql)
            .ToList();

        copies.Should().ContainSingle(
            "without it the migration is a silent data loss: every market a farmer had already chosen "
            + "disappears with the column");
    }

    [Fact]
    public void Up_CopiesEveryChosenMarket_IntoTheChildTable()
    {
        var sql = PerCropWatchMarketsAndPlantedDate.CopyPreferredMarketsSql;

        sql.Should().Contain("INSERT INTO [UserCropWatchMarkets]");
        sql.Should().Contain("FROM [UserCropWatchlist]");
        sql.Should().Contain("[PreferredMarketId] IS NOT NULL",
            "rows with no chosen market must produce no child row — 'no market' is a state, not a market");
        sql.Should().Contain("[CreatedAtUtc]",
            "the migrated market keeps the parent's own chosen-at time so it stays first in the "
            + "oldest-chosen display order");
    }

    [Fact]
    public void Up_RunsTheCopyBeforeTheColumnItReadsIsDropped()
    {
        var ops = UpOperations();

        var copyAt = IndexOf(ops, op =>
            op is SqlOperation s && s.Sql == PerCropWatchMarketsAndPlantedDate.CopyPreferredMarketsSql);
        var dropAt = IndexOf(ops, op =>
            op is DropColumnOperation d && d.Table == "UserCropWatchlist" && d.Name == "PreferredMarketId");

        copyAt.Should().BeGreaterThan(-1);
        dropAt.Should().BeGreaterThan(-1);
        copyAt.Should().BeLessThan(dropAt,
            "the scaffolder puts the DropColumn first; re-ordering it back would read an already-dropped "
            + "column and lose every farmer's market");
    }

    [Fact]
    public void Up_CreatesTheChildTableBeforeCopyingIntoIt()
    {
        var ops = UpOperations();

        var createAt = IndexOf(ops, op =>
            op is CreateTableOperation c && c.Name == "UserCropWatchMarkets");
        var copyAt = IndexOf(ops, op =>
            op is SqlOperation s && s.Sql == PerCropWatchMarketsAndPlantedDate.CopyPreferredMarketsSql);

        createAt.Should().BeGreaterThan(-1);
        createAt.Should().BeLessThan(copyAt);
    }

    [Fact]
    public void Up_CascadesTheChildTableFromItsParent_AndRestrictsMarkets()
    {
        var table = UpOperations()
            .OfType<CreateTableOperation>()
            .Single(c => c.Name == "UserCropWatchMarkets");

        table.ForeignKeys.Single(f => f.PrincipalTable == "UserCropWatchlist")
            .OnDelete.Should().Be(ReferentialAction.Cascade,
                "deleting an account cascades Users -> watchlist -> markets; an orphan child row would be "
                + "personal data with nothing to scope it to");

        table.ForeignKeys.Single(f => f.PrincipalTable == "Markets")
            .OnDelete.Should().Be(ReferentialAction.Restrict,
                "a market a farmer is watching cannot be deleted out from under them");
    }

    [Fact]
    public void Up_DoesNotConstrainTheMarketCountInTheDatabase()
    {
        // The 3-market cap is a product rule answered as the too_many_markets wire code. A CHECK
        // constraint could only reach the farmer as a provider exception, i.e. a 500 for a limit they hit.
        UpOperations()
            .OfType<CreateTableOperation>()
            .Single(c => c.Name == "UserCropWatchMarkets")
            .CheckConstraints.Should().BeEmpty();
    }

    [Fact]
    public void Down_RestoresAMarketBeforeDroppingTheTableItReadsFrom()
    {
        var ops = DownOperations();

        var addColumnAt = IndexOf(ops, op =>
            op is AddColumnOperation a && a.Name == "PreferredMarketId");
        var restoreAt = IndexOf(ops, op => op is SqlOperation s && s.Sql.Contains("UPDATE"));
        var dropTableAt = IndexOf(ops, op =>
            op is DropTableOperation d && d.Name == "UserCropWatchMarkets");

        addColumnAt.Should().BeGreaterThan(-1);
        restoreAt.Should().BeGreaterThan(addColumnAt, "there is nowhere to restore to until the column exists");
        restoreAt.Should().BeLessThan(dropTableAt, "and nothing to restore from once the table is gone");
    }
}
