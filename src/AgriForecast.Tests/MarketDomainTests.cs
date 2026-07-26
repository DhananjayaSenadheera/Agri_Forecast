using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// Regression tests for the market-dimension entities: Market, PriceObservation and MarketType.
/// PriceObservation.Create's asOfUtc guard is the most important test here: it is the leakage safeguard
/// that stops a forgetful ingestion path writing AsOfUtc = 0001-01-01, which would make the row
/// "already published" in every as-of window.
/// </summary>
public class MarketDomainTests
{
    // PriceObservation.Create — happy path.

    private static (Guid marketId, string commodityName, DateOnly observedDate, DateTime asOfUtc, string source)
        ValidArgs() => (
            Guid.NewGuid(),
            "Carrot (Local)",
            new DateOnly(2026, 6, 1),
            new DateTime(2026, 6, 2, 5, 30, 0, DateTimeKind.Utc),
            "HARTI");

    [Fact]
    public void PriceObservation_Create_AssignsNonEmptyUniqueId()
    {
        var a = ValidArgs();

        var obs1 = PriceObservation.Create(a.marketId, a.commodityName, a.observedDate, a.asOfUtc, a.source);
        var obs2 = PriceObservation.Create(a.marketId, a.commodityName, a.observedDate, a.asOfUtc, a.source);

        obs1.Id.Should().NotBe(Guid.Empty);
        obs2.Id.Should().NotBe(Guid.Empty);
        obs1.Id.Should().NotBe(obs2.Id, "each create call must mint a fresh identity");
    }

    [Fact]
    public void PriceObservation_Create_CopiesRequiredFieldsExactly()
    {
        var a = ValidArgs();

        var obs = PriceObservation.Create(a.marketId, a.commodityName, a.observedDate, a.asOfUtc, a.source);

        obs.MarketId.Should().Be(a.marketId);
        obs.ExternalCommodityName.Should().Be(a.commodityName);
        obs.ObservedDate.Should().Be(a.observedDate);
        obs.AsOfUtc.Should().Be(a.asOfUtc);
        obs.Source.Should().Be(a.source);
    }

    [Fact]
    public void PriceObservation_Create_CopiesOptionalFields_WhenProvided()
    {
        var a = ValidArgs();
        var cropId = Guid.NewGuid();

        var obs = PriceObservation.Create(
            a.marketId, a.commodityName, a.observedDate, a.asOfUtc, a.source,
            externalCommodityId: 42,
            cropId: cropId,
            wholesalePrice: 100.50m,
            retailPrice: 120.00m,
            minPrice: 95.00m,
            maxPrice: 130.00m,
            arrivalsKg: 5000.25m);

        obs.ExternalCommodityId.Should().Be(42);
        obs.CropId.Should().Be(cropId);
        obs.WholesalePrice.Should().Be(100.50m);
        obs.RetailPrice.Should().Be(120.00m);
        obs.MinPrice.Should().Be(95.00m);
        obs.MaxPrice.Should().Be(130.00m);
        obs.ArrivalsKg.Should().Be(5000.25m);
    }

    [Fact]
    public void PriceObservation_Create_NullablePricesStayNull_WhenNotProvided()
    {
        // Partial bulletins (e.g. arrivals-only, or wholesale-only) must insert cleanly
        // without coercing missing fields to zero.
        var a = ValidArgs();

        var obs = PriceObservation.Create(a.marketId, a.commodityName, a.observedDate, a.asOfUtc, a.source);

        obs.ExternalCommodityId.Should().BeNull();
        obs.CropId.Should().BeNull();
        obs.WholesalePrice.Should().BeNull();
        obs.RetailPrice.Should().BeNull();
        obs.MinPrice.Should().BeNull();
        obs.MaxPrice.Should().BeNull();
        obs.ArrivalsKg.Should().BeNull();
    }

    [Fact]
    public void PriceObservation_Create_StampsRetrievedAtUtc_ToUtcNow()
    {
        var a = ValidArgs();

        var before = DateTime.UtcNow;
        var obs = PriceObservation.Create(a.marketId, a.commodityName, a.observedDate, a.asOfUtc, a.source);
        var after = DateTime.UtcNow;

        obs.RetrievedAtUtc.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void PriceObservation_Create_RetrievedAtUtc_IsNeverCallerSupplied()
    {
        // RetrievedAtUtc has no constructor parameter at all — this pins that fact so a
        // future refactor can't quietly add a caller-supplied override (it is audit-only,
        // never a feature, per the entity's own doc comment).
        typeof(PriceObservation)
            .GetMethod(nameof(PriceObservation.Create))!
            .GetParameters()
            .Select(p => p.Name)
            .Should().NotContain("retrievedAtUtc");
    }

    // PriceObservation.Create — THE LEAKAGE GUARD.

    [Fact]
    public void PriceObservation_Create_Throws_WhenAsOfUtcIsDefault()
    {
        // This is the leakage guard. A default(DateTime) AsOfUtc == 0001-01-01, which would
        // be "already published" in every as-of window the ML layer joins on — a silent
        // look-ahead leak that would be invisible in a spot-check of the data. This must
        // never be relaxed to a silent default.
        var a = ValidArgs();

        var act = () => PriceObservation.Create(a.marketId, a.commodityName, a.observedDate, default, a.source);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("asOfUtc");
    }

    [Fact]
    public void PriceObservation_Create_Throws_WhenMarketIdIsEmpty()
    {
        var a = ValidArgs();

        var act = () => PriceObservation.Create(Guid.Empty, a.commodityName, a.observedDate, a.asOfUtc, a.source);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("marketId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void PriceObservation_Create_Throws_WhenExternalCommodityNameIsEmptyOrWhitespace(string? name)
    {
        var a = ValidArgs();

        var act = () => PriceObservation.Create(a.marketId, name!, a.observedDate, a.asOfUtc, a.source);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("externalCommodityName");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void PriceObservation_Create_Throws_WhenSourceIsEmptyOrWhitespace(string? source)
    {
        var a = ValidArgs();

        var act = () => PriceObservation.Create(a.marketId, a.commodityName, a.observedDate, a.asOfUtc, source!);

        act.Should().Throw<ArgumentException>()
            .And.ParamName.Should().Be("source");
    }

    // PriceObservation.Create — AsOfUtc and ObservedDate stay independent.

    [Fact]
    public void PriceObservation_Create_KeepsAsOfUtcAndObservedDateDistinct_WhenPublishedLate()
    {
        // A price observation FOR June 1st, published June 2nd (D+1 bulletin lag) must keep
        // both vintages distinct — never collapse AsOfUtc to ObservedDate or vice versa.
        // This is what the ML layer's as-of join relies on to avoid treating the price as
        // "known" on the day it was actually for.
        var marketId = Guid.NewGuid();
        var observedDate = new DateOnly(2026, 6, 1);
        var asOfUtc = new DateTime(2026, 6, 2, 6, 0, 0, DateTimeKind.Utc);

        var obs = PriceObservation.Create(marketId, "Carrot (Local)", observedDate, asOfUtc, "HARTI");

        obs.ObservedDate.Should().Be(observedDate);
        obs.AsOfUtc.Should().Be(asOfUtc);
        obs.AsOfUtc.Should().BeAfter(obs.ObservedDate.ToDateTime(TimeOnly.MinValue),
            "the bulletin vintage lags the economic event date in this fixture, by design");
    }

    [Fact]
    public void PriceObservation_Create_AllowsAsOfUtcSameDayAsObservedDate()
    {
        // Same-day publication is a valid, common case too — no artificial minimum lag
        // should be enforced by the entity.
        var observedDate = new DateOnly(2026, 6, 1);
        var asOfUtc = new DateTime(2026, 6, 1, 18, 0, 0, DateTimeKind.Utc);

        var obs = PriceObservation.Create(Guid.NewGuid(), "Carrot (Local)", observedDate, asOfUtc, "HARTI");

        obs.ObservedDate.Should().Be(observedDate);
        obs.AsOfUtc.Should().Be(asOfUtc);
    }

    // PriceObservation.AssignCrop.

    [Fact]
    public void PriceObservation_AssignCrop_SetsCropId()
    {
        var a = ValidArgs();
        var obs = PriceObservation.Create(a.marketId, a.commodityName, a.observedDate, a.asOfUtc, a.source);
        obs.CropId.Should().BeNull();
        var cropId = Guid.NewGuid();

        obs.AssignCrop(cropId);

        obs.CropId.Should().Be(cropId);
    }

    [Fact]
    public void PriceObservation_AssignCrop_Throws_WhenCropIdIsEmpty()
    {
        // R1.1 P1 guard: Guid.Empty is never a valid mapping target — AssignCrop must reject
        // it rather than silently blanking the crop. (Was previously pinned as a no-guard smell;
        // this rewrite pins the new deliberate guard.)
        var a = ValidArgs();
        var obs = PriceObservation.Create(a.marketId, a.commodityName, a.observedDate, a.asOfUtc, a.source);

        var act = () => obs.AssignCrop(Guid.Empty);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("cropId");
    }

    [Fact]
    public void PriceObservation_AssignCrop_Throws_WhenAlreadyAssignedAndNotOverwriting()
    {
        // R1.1 P1 guard: the self-heal path must NOT silently re-map an already-assigned
        // observation. A second AssignCrop without overwrite:true is a defect, not a no-op.
        var a = ValidArgs();
        var obs = PriceObservation.Create(a.marketId, a.commodityName, a.observedDate, a.asOfUtc, a.source);
        var first = Guid.NewGuid();
        obs.AssignCrop(first);

        var act = () => obs.AssignCrop(Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>();
        obs.CropId.Should().Be(first, "the rejected re-map must not have mutated the crop");
    }

    [Fact]
    public void PriceObservation_AssignCrop_Remaps_WhenOverwriteTrue()
    {
        // A deliberate re-map (e.g. a corrected canonical mapping) is allowed via overwrite:true.
        var a = ValidArgs();
        var obs = PriceObservation.Create(a.marketId, a.commodityName, a.observedDate, a.asOfUtc, a.source);
        obs.AssignCrop(Guid.NewGuid());
        var corrected = Guid.NewGuid();

        obs.AssignCrop(corrected, overwrite: true);

        obs.CropId.Should().Be(corrected);
    }

    // PriceObservation.Create — unit quarantine.

    [Fact]
    public void PriceObservation_Create_UnitIsUnconfirmedByDefault()
    {
        // Fail-closed: a row is quarantined (unit unproven) until ingestion explicitly confirms
        // it. The default must be FALSE and the unit fields NULL — never assume LKR/kg.
        var a = ValidArgs();

        var obs = PriceObservation.Create(a.marketId, a.commodityName, a.observedDate, a.asOfUtc, a.source);

        obs.IsUnitConfirmed.Should().BeFalse();
        obs.UnitRaw.Should().BeNull();
        obs.UnitConversionFactor.Should().BeNull();
    }

    [Fact]
    public void PriceObservation_Create_CopiesUnitFields_WhenProvidedAndConfirmed()
    {
        var a = ValidArgs();

        var obs = PriceObservation.Create(
            a.marketId, a.commodityName, a.observedDate, a.asOfUtc, a.source,
            unitRaw: "Rs/kg",
            unitConversionFactor: 1.0m,
            isUnitConfirmed: true);

        obs.UnitRaw.Should().Be("Rs/kg");
        obs.UnitConversionFactor.Should().Be(1.0m);
        obs.IsUnitConfirmed.Should().BeTrue();
    }

    // Market.CreateNew — happy path.

    [Fact]
    public void Market_CreateNew_AssignsNonEmptyUniqueId()
    {
        var a = Market.CreateNew("Dambulla DEC", "Matale", MarketType.DEC);
        var b = Market.CreateNew("Dambulla DEC", "Matale", MarketType.DEC);

        a.Id.Should().NotBe(Guid.Empty);
        b.Id.Should().NotBe(Guid.Empty);
        a.Id.Should().NotBe(b.Id, "each create call must mint a fresh identity");
    }

    [Fact]
    public void Market_CreateNew_CopiesNameDistrictAndMarketType()
    {
        var market = Market.CreateNew("Dambulla DEC", "Matale", MarketType.DEC);

        market.Name.Should().Be("Dambulla DEC");
        market.District.Should().Be("Matale");
        market.MarketType.Should().Be(MarketType.DEC);
    }

    [Fact]
    public void Market_CreateNew_SetsIsActiveTrue()
    {
        var market = Market.CreateNew("Dambulla DEC", "Matale", MarketType.DEC);

        market.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Market_CreateNew_SetsCreatedAndUpdatedAt_ToUtcNow()
    {
        var before = DateTime.UtcNow;
        var market = Market.CreateNew("Dambulla DEC", "Matale", MarketType.DEC);
        var after = DateTime.UtcNow;

        market.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        market.UpdatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Market_CreateNew_AllowsNullDistrict()
    {
        // NationalAggregate pseudo-markets (e.g. CBSL national average) are explicitly not
        // tied to a location, per the entity's doc comment — District must accept null.
        var market = Market.CreateNew("CBSL National Average", null, MarketType.NationalAggregate);

        market.District.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Market_CreateNew_Throws_WhenNameIsEmptyOrWhitespace(string? name)
    {
        // R1.1 P1 guard: a market with no name is meaningless. CreateNew now rejects
        // empty/whitespace/null names (was previously pinned as a no-guard smell).
        var act = () => Market.CreateNew(name!, "Matale", MarketType.DEC);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("name");
    }

    [Fact]
    public void Market_CreateNew_IsEconomicCenterDefaultsFalse()
    {
        // R2 D-DF3: a plain market is NOT an economic centre. The flag must default false so
        // ingestion-provisioned and CRUD-created markets stay plain unless explicitly promoted.
        var market = Market.CreateNew("Dambulla DEC", "Matale", MarketType.DEC);

        market.IsEconomicCenter.Should().BeFalse();
    }

    [Fact]
    public void Market_CreateNew_HonorsIsEconomicCenter_WhenTrue()
    {
        // A Dedicated Economic Centre registered through the factory carries the flag set —
        // this is how "register a new economic centre" becomes a Markets row post-merge.
        var market = Market.CreateNew("New DEC", "Kandy", MarketType.DEC, isEconomicCenter: true);

        market.IsEconomicCenter.Should().BeTrue();
    }

    // Market.AssignCode.

    [Fact]
    public void Market_AssignCode_StampsCode_WhenNotYetAssigned()
    {
        var market = Market.CreateNew("Dambulla DEC", "Matale", MarketType.DEC);

        market.AssignCode("MKT00000007");

        market.MarketCode.Should().Be("MKT00000007");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Market_AssignCode_Throws_WhenCodeIsEmptyOrWhitespace(string? code)
    {
        var market = Market.CreateNew("Dambulla DEC", "Matale", MarketType.DEC);

        var act = () => market.AssignCode(code!);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("marketCode");
    }

    [Fact]
    public void Market_AssignCode_Throws_WhenAlreadyAssigned()
    {
        // Code is a one-time stamp — refuse a silent re-stamp.
        var market = Market.CreateNew("Dambulla DEC", "Matale", MarketType.DEC);
        market.AssignCode("MKT00000007");

        var act = () => market.AssignCode("MKT00000099");

        act.Should().Throw<InvalidOperationException>();
        market.MarketCode.Should().Be("MKT00000007");
    }

    [Fact]
    public void Market_CreateNew_MarketCodeIsLeftUnassigned()
    {
        // MarketCode is assigned by the create handler after construction (mirrors
        // EconomicCenter.CreateNew / Crop.CreateForManualEntry) — CreateNew itself must not
        // set it.
        var market = Market.CreateNew("Dambulla DEC", "Matale", MarketType.DEC);

        market.MarketCode.Should().Be(string.Empty);
    }

    // Market.ApplyUpdate.

    private static Market ExistingMarket() => Market.CreateNew("Original", "OrigDistrict", MarketType.Wholesale);

    [Fact]
    public void Market_ApplyUpdate_OverwritesNameDistrictMarketTypeAndIsActive()
    {
        var existing = ExistingMarket();

        existing.ApplyUpdate("Renamed", "NewDistrict", MarketType.DEC, false);

        existing.Name.Should().Be("Renamed");
        existing.District.Should().Be("NewDistrict");
        existing.MarketType.Should().Be(MarketType.DEC);
        existing.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Market_ApplyUpdate_OverwritesDistrictWithNull_Unconditionally()
    {
        var existing = ExistingMarket();

        existing.ApplyUpdate("Renamed", null, MarketType.DEC, true);

        existing.District.Should().BeNull();
    }

    [Fact]
    public void Market_ApplyUpdate_RefreshesUpdatedAt()
    {
        var existing = ExistingMarket();
        existing.UpdatedAt = DateTime.UtcNow.AddDays(-5);

        var before = DateTime.UtcNow;
        existing.ApplyUpdate("Renamed", "NewDistrict", MarketType.DEC, true);
        var after = DateTime.UtcNow;

        existing.UpdatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void Market_ApplyUpdate_ThreadsIsEconomicCenter()
    {
        // The flag is settable through the update path too, so a market can be promoted to /
        // demoted from an economic centre. Defaults false when the arg is omitted (existing
        // 4-arg callers stay behaviourally unchanged).
        var existing = ExistingMarket();
        existing.IsEconomicCenter.Should().BeFalse();

        existing.ApplyUpdate("Renamed", "NewDistrict", MarketType.DEC, true, isEconomicCenter: true);
        existing.IsEconomicCenter.Should().BeTrue();

        existing.ApplyUpdate("Renamed", "NewDistrict", MarketType.DEC, true);
        existing.IsEconomicCenter.Should().BeFalse("the 4-arg overload leaves it at the default false");
    }

    [Fact]
    public void Market_ApplyUpdate_DoesNotChangeIdOrCreatedAt()
    {
        var existing = ExistingMarket();
        var originalId = existing.Id;
        var originalCreatedAt = existing.CreatedAt;

        existing.ApplyUpdate("Renamed", "NewDistrict", MarketType.DEC, false);

        existing.Id.Should().Be(originalId);
        existing.CreatedAt.Should().Be(originalCreatedAt);
    }

    // MarketType enum — persisted-int pinning.

    // MarketType is stored as a plain int column, so any reorder or insertion silently reassigns the numeric
    // value of everything after it and corrupts every persisted row without throwing. If this test fails, do
    // not "fix" the test — the fix belongs in a migration or backfill.
    [Theory]
    [InlineData(MarketType.Wholesale, 0)]
    [InlineData(MarketType.Retail, 1)]
    [InlineData(MarketType.DEC, 2)]
    [InlineData(MarketType.NationalAggregate, 3)]
    public void MarketType_NumericValues_ArePinned(MarketType value, int expected)
    {
        ((int)value).Should().Be(expected);
    }

    [Fact]
    public void MarketType_HasExactlyFourMembers()
    {
        // Guards against a silent insertion (e.g. a new member added in the middle) that
        // wouldn't be caught by the pinned-value theory above if it also shifted a later one
        // in a way that happened to still pass by coincidence — belt and suspenders.
        Enum.GetValues<MarketType>().Should().HaveCount(4);
    }
}
