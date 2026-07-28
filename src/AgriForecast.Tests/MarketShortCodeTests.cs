using System.Reflection;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Infrastructure.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace AgriForecast.Tests;

/// <summary>
/// Guards for Markets.ShortCode, the short display code rendered beside a market name.
/// <para>
/// Two things are load-bearing. (1) The seed: every seeded market must carry a distinct, non-empty,
/// short code — a blank or duplicated code shows up as an unlabelled or ambiguous chip in the farmer UI,
/// and duplicates would be rejected by the unique index at deploy time rather than here. (2) The mapping:
/// the code is nvarchar(8) NOT NULL and unique among ASSIGNED codes only — the filter is what lets a
/// market be registered without a display code, so the filter is pinned, not just the uniqueness.
/// </para>
/// ShortCode is display-only: no test here (and no code anywhere) may key, join or forecast on it.
/// Style mirrors UserCropWatchlistTests.cs (model built against the SQL Server provider, never connected).
/// </summary>
public class MarketShortCodeTests
{
    // EF mapping.

    private static IEntityType MarketEntityType()
    {
        var options = new DbContextOptionsBuilder<AgriForecastDbContext>()
            .UseSqlServer("Server=(model-only);Database=none;")
            .Options;
        using var ctx = new AgriForecastDbContext(options);
        return ctx.Model.FindEntityType(typeof(Market))!;
    }

    [Fact]
    public void Mapping_ShortCode_IsRequiredNvarchar8()
    {
        var property = MarketEntityType().FindProperty("ShortCode")!;

        property.IsNullable.Should().BeFalse("an unassigned code is '' — never NULL, so the FE needs no null check");
        property.GetMaxLength().Should().Be(8);
    }

    [Fact]
    public void Mapping_ShortCode_HasUniqueIndexFilteredToAssignedCodes()
    {
        var index = MarketEntityType().GetIndexes()
            .Single(i => i.GetDatabaseName() == "UX_Markets_ShortCode");

        index.IsUnique.Should().BeTrue("two markets sharing a display code would be indistinguishable in the UI");
        index.Properties.Select(p => p.Name).Should().Equal("ShortCode");
        index.GetFilter().Should().Be("[ShortCode] <> ''",
            "markets registered without a display code all store '' — an unfiltered unique index would let "
            + "the first of them block every later registration");
    }

    [Fact]
    public void Mapping_KeepsMarketCodeUnique_Separately()
    {
        // ShortCode is display-only and must not have displaced MarketCode as the business key.
        var index = MarketEntityType().GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == "MarketCode");

        index.IsUnique.Should().BeTrue();
    }

    // The HasData seed.

    // HasData rows live on the DESIGN-TIME model; the runtime model is read-optimized and throws on
    // GetSeedData, so this deliberately does not reuse MarketEntityType() above.
    private static IReadOnlyList<IDictionary<string, object?>> SeededMarkets()
    {
        var options = new DbContextOptionsBuilder<AgriForecastDbContext>()
            .UseSqlServer("Server=(model-only);Database=none;")
            .Options;
        using var ctx = new AgriForecastDbContext(options);
        return ctx.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(Market))!
            .GetSeedData()
            .ToList();
    }

    [Fact]
    public void Seed_EveryMarket_HasANonEmptyShortCode()
    {
        var seed = SeededMarkets();
        seed.Should().NotBeEmpty();

        foreach (var row in seed)
        {
            var name = (string)row["Name"]!;
            row.Should().ContainKey("ShortCode", $"{name} must be seeded with a display code");
            ((string?)row["ShortCode"]).Should().NotBeNullOrWhiteSpace(
                $"{name} would otherwise render as an unlabelled chip");
        }
    }

    [Fact]
    public void Seed_ShortCodes_AreDistinct()
    {
        var codes = SeededMarkets().Select(r => (string)r["ShortCode"]!).ToList();

        codes.Should().OnlyHaveUniqueItems(
            "a duplicate makes two markets indistinguishable and violates UX_Markets_ShortCode on deploy");
    }

    [Fact]
    public void Seed_ShortCodes_AreShortAndUpperCaseAlphanumeric()
    {
        foreach (var row in SeededMarkets())
        {
            var code = (string)row["ShortCode"]!;
            // 5 is the practical UI budget for the chip; the column allows 8 for future registrations.
            code.Length.Should().BeInRange(2, 5, $"'{code}' must stay readable in a narrow chip");
            code.Should().MatchRegex("^[A-Z0-9]+$", "codes are normalized upper-case letters and digits");
        }
    }

    [Fact]
    public void Seed_Dambulla_IsCodedDEC()
    {
        // Owner-fixed: the reference DEC is the one code that is not free to change.
        var dambulla = SeededMarkets().Single(r => (string)r["MarketCode"]! == "MKT00000001");

        ((string)dambulla["ShortCode"]!).Should().Be("DEC");
        ((string)dambulla["Name"]!).Should().Be("Dambulla Dedicated Economic Centre");
    }

    // Seed <-> migration parity. HasData is what a fresh database gets; a DEPLOYED database only ever sees
    // what a migration wrote. Editing a code in HasData without adding a migration leaves every test green
    // while production keeps the old value — this is the test that refuses that.

    /// <summary>
    /// Replays every migration in the Infrastructure assembly, in migration-id order, and returns the
    /// Markets.ShortCode values a deployed database would hold. Only operations that touch the ShortCode
    /// column count, so a later migration that renames a code simply overwrites the earlier value and this
    /// stays true — the test fails only while a HasData edit has no migration behind it.
    /// </summary>
    private static IReadOnlyDictionary<Guid, string> ShortCodesWrittenByMigrations()
    {
        var state = new Dictionary<Guid, string>();

        var migrations = typeof(AgriForecastDbContext).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => new { Type = t, Id = t.GetCustomAttribute<MigrationAttribute>()?.Id })
            .Where(m => m.Id is not null)
            .OrderBy(m => m.Id, StringComparer.Ordinal);

        foreach (var migration in migrations)
        {
            var operations = ((Migration)Activator.CreateInstance(migration.Type)!).UpOperations;

            foreach (var operation in operations)
            {
                switch (operation)
                {
                    // The seeded rows already exist, so HasData changes arrive as UpdateData keyed by Id.
                    case UpdateDataOperation update
                        when update.Table == "Markets"
                            && update.KeyColumns.SequenceEqual(new[] { "Id" })
                            && update.Columns.Contains("ShortCode"):
                        ApplyRows(
                            state,
                            idSource: update.KeyValues, idIndex: 0,
                            valueSource: update.Values,
                            codeIndex: Array.IndexOf(update.Columns, "ShortCode"));
                        break;

                    // A future seeded market arrives as InsertData carrying the column outright.
                    case InsertDataOperation insert
                        when insert.Table == "Markets"
                            && insert.Columns.Contains("Id")
                            && insert.Columns.Contains("ShortCode"):
                        ApplyRows(
                            state,
                            idSource: insert.Values, idIndex: Array.IndexOf(insert.Columns, "Id"),
                            valueSource: insert.Values,
                            codeIndex: Array.IndexOf(insert.Columns, "ShortCode"));
                        break;
                }
            }
        }

        return state;

        static void ApplyRows(
            Dictionary<Guid, string> state,
            object?[,] idSource, int idIndex, object?[,] valueSource, int codeIndex)
        {
            for (var row = 0; row < valueSource.GetLength(0); row++)
                state[(Guid)idSource[row, idIndex]!] = (string)valueSource[row, codeIndex]!;
        }
    }

    [Fact]
    public void Seed_ShortCodes_AreAllCarriedByAMigration()
    {
        var deployed = ShortCodesWrittenByMigrations();

        foreach (var row in SeededMarkets())
        {
            var id = (Guid)row["Id"]!;
            var name = (string)row["Name"]!;
            var seeded = (string)row["ShortCode"]!;

            deployed.Should().ContainKey(id,
                $"{name}'s short code only reaches an existing database through a migration — add one");
            deployed[id].Should().Be(seeded,
                $"{name} is seeded as '{seeded}' but the migrations write "
                + $"'{(deployed.TryGetValue(id, out var v) ? v : "<nothing>")}': a renamed code needs a new "
                + "migration to carry it, or fresh and deployed databases silently disagree");
        }
    }

    [Fact]
    public void Migrations_DoNotWriteShortCodesForMarketsTheSeedDoesNotKnow()
    {
        // The other direction: a migration that codes a row the seed has since dropped would leave a value
        // in deployed databases that no test or seed accounts for.
        var seededIds = SeededMarkets().Select(r => (Guid)r["Id"]!).ToHashSet();

        ShortCodesWrittenByMigrations().Keys.Should().OnlyContain(id => seededIds.Contains(id));
    }

    [Fact]
    public void Seed_ShortCodes_DoNotCollideWithMarketCodes()
    {
        // A sanity guard against anyone ever treating ShortCode as the business key: the two code spaces
        // are disjoint, so a lookup by the wrong one fails loudly instead of matching the wrong row.
        var shortCodes = SeededMarkets().Select(r => (string)r["ShortCode"]!).ToHashSet();
        var marketCodes = SeededMarkets().Select(r => (string)r["MarketCode"]!).ToHashSet();

        shortCodes.Overlaps(marketCodes).Should().BeFalse();
    }

    // Domain factory / update path normalization.

    [Fact]
    public void CreateNew_WithoutShortCode_LeavesItUnassignedRatherThanInventingOne()
    {
        var market = Market.CreateNew("Freshly Registered Market", "Colombo", MarketType.Wholesale);

        market.ShortCode.Should().BeEmpty();
    }

    [Theory]
    [InlineData("kep", "KEP")]
    [InlineData("  KeP  ", "KEP")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void CreateNew_NormalizesShortCode(string? supplied, string expected)
    {
        var market = Market.CreateNew(
            "Keppetipola Dedicated Economic Centre", "Badulla", MarketType.DEC, shortCode: supplied);

        market.ShortCode.Should().Be(expected,
            "case and padding variants must not become two codes that both satisfy the unique index");
    }

    [Fact]
    public void ApplyUpdate_WithoutShortCode_KeepsTheExistingCode()
    {
        var market = Market.CreateNew("Kandy", "Kandy", MarketType.Wholesale, shortCode: "KAN");

        market.ApplyUpdate("Kandy (HARTI wholesale)", "Kandy", MarketType.Wholesale, isActive: true);

        market.ShortCode.Should().Be("KAN",
            "an update of some other field must never blank a market's display code");
    }

    [Fact]
    public void ApplyUpdate_WithShortCode_OverwritesItNormalized()
    {
        var market = Market.CreateNew("Kandy", "Kandy", MarketType.Wholesale, shortCode: "KAN");

        market.ApplyUpdate(
            "Kandy", "Kandy", MarketType.Wholesale, isActive: true, shortCode: " kdy ");

        market.ShortCode.Should().Be("KDY");
    }
}
