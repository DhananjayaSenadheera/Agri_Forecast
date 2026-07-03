using AgriForecast.Domain.Entities;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// Unit tests for the CommodityAlias entity (R1.1 P1, ClickUp 86cahef4z) — the
/// version-controlled source-label -> canonical-crop mapping table that replaces hardcoded
/// aliases in parser logic (PRD risk R5). Style mirrors MarketDomainTests.cs.
/// </summary>
public class CommodityAliasTests
{
    [Fact]
    public void CreateNew_AssignsNonEmptyUniqueId()
    {
        var cropId = Guid.NewGuid();

        var a = CommodityAlias.CreateNew("Beans", cropId, "HARTI", "en");
        var b = CommodityAlias.CreateNew("Beans", cropId, "HARTI", "en");

        a.Id.Should().NotBe(Guid.Empty);
        b.Id.Should().NotBe(Guid.Empty);
        a.Id.Should().NotBe(b.Id, "each create call must mint a fresh identity");
    }

    [Fact]
    public void CreateNew_CopiesAllFields()
    {
        var cropId = Guid.NewGuid();

        var alias = CommodityAlias.CreateNew("Ladies Fingers", cropId, "HARTI", "en");

        alias.Alias.Should().Be("Ladies Fingers");
        alias.CropId.Should().Be(cropId);
        alias.Source.Should().Be("HARTI");
        alias.Language.Should().Be("en");
    }

    [Fact]
    public void CreateNew_IsActiveByDefault()
    {
        var alias = CommodityAlias.CreateNew("Beans", Guid.NewGuid());

        alias.IsActive.Should().BeTrue();
    }

    [Fact]
    public void CreateNew_StampsCreatedAtUtc_ToUtcNow()
    {
        var before = DateTime.UtcNow;
        var alias = CommodityAlias.CreateNew("Beans", Guid.NewGuid());
        var after = DateTime.UtcNow;

        alias.CreatedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void CreateNew_AllowsNullSourceAndLanguage()
    {
        // Source = NULL means the alias is global (applies to all sources); Language is optional.
        var alias = CommodityAlias.CreateNew("Bonchi", Guid.NewGuid());

        alias.Source.Should().BeNull();
        alias.Language.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CreateNew_Throws_WhenAliasIsEmptyOrWhitespace(string? alias)
    {
        var act = () => CommodityAlias.CreateNew(alias!, Guid.NewGuid());

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("alias");
    }

    [Fact]
    public void CreateNew_Throws_WhenCropIdIsEmpty()
    {
        var act = () => CommodityAlias.CreateNew("Beans", Guid.Empty);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("cropId");
    }
}
