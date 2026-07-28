using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Infrastructure.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

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
