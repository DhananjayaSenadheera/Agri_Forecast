using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgriForecast.API.Controllers;
using AgriForecast.Application.Requests.Portfolio.Commands.RecordSale;
using AgriForecast.Application.Requests.Portfolio.Commands.UpdateSale;
using AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistEntry;
using AgriForecast.Application.Requests.Portfolio.Common;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// The PUT /api/portfolio/watchlist/{cropId} body, deserialized the way ASP.NET Core actually deserializes
/// it — <see cref="JsonSerializerDefaults.Web"/>, from raw JSON text.
/// <para>
/// EVERY OTHER TEST BUILDS THE COMMAND IN C#, which cannot tell the two states apart that matter most here:
/// a request that OMITS <c>plantedDate</c> and one that sends it as <c>null</c> both leave the property
/// null, and only the wire distinguishes them. The tri-state is carried by a hand-written setter that flips
/// <c>PlantedDateSpecified</c> whenever System.Text.Json touches it, so turning the property back into an
/// auto-property — or swapping the serializer for one that does not honour the setter or the
/// <c>[JsonIgnore]</c> — silently makes "clear my planting date" and "leave it alone" the same request.
/// These tests are what fails when that happens.
/// </para>
/// </summary>
public class PortfolioWireContractTests
{
    // The same options ASP.NET Core's JSON input formatter uses: camelCase, case-insensitive.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static UpdateWatchlistEntryCommand Bind(string json)
        => JsonSerializer.Deserialize<UpdateWatchlistEntryCommand>(json, Web)!;

    [Fact]
    public void AnEmptyBody_LeavesThePlantingDateUnspecified()
    {
        var command = Bind("{}");

        command.PlantedDateSpecified.Should().BeFalse(
            "no plantedDate key means 'leave it alone'; treating it as null would wipe the farmer's date "
            + "every time they edited their markets");
        command.PlantedDate.Should().BeNull();
        command.MarketIds.Should().BeNull("omitted marketIds likewise means 'leave them alone'");
    }

    [Fact]
    public void AnExplicitNull_IsSpecified_AndClears()
    {
        var command = Bind("""{"plantedDate":null}""");

        command.PlantedDateSpecified.Should().BeTrue(
            "an explicit null is the farmer un-telling us, which is a real request the handler must apply");
        command.PlantedDate.Should().BeNull();
    }

    [Fact]
    public void AValue_IsSpecified_AndCarriesTheDate()
    {
        var command = Bind("""{"plantedDate":"2026-05-01"}""");

        command.PlantedDateSpecified.Should().BeTrue();
        command.PlantedDate.Should().Be(new DateOnly(2026, 5, 1));
    }

    [Fact]
    public void TheSpecifiedFlagCannotBeForgedFromTheWire()
    {
        var command = Bind("""{"plantedDateSpecified":true}""");

        command.PlantedDateSpecified.Should().BeFalse(
            "the flag is a side effect of the setter, never bound: a caller who could set it directly "
            + "would be able to clear a date without sending one");
        command.PlantedDate.Should().BeNull();
    }

    [Fact]
    public void AnEmptyMarketArray_BindsAsAnEmptyList_NotNull()
    {
        var command = Bind("""{"marketIds":[]}""");

        // [] and omitted are DIFFERENT requests: [] clears every market, omitted leaves them alone. If
        // these ever bound to the same thing, "clear my markets" would become impossible to express.
        command.MarketIds.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void TheFullBody_BindsBothFieldsTogether()
    {
        var command = Bind(
            """{"marketIds":["b2a20001-0000-0000-0000-000000000002"],"plantedDate":"2026-05-01"}""");

        command.MarketIds.Should().ContainSingle()
            .Which.Should().Be(Guid.Parse("b2a20001-0000-0000-0000-000000000002"));
        command.PlantedDateSpecified.Should().BeTrue();
        command.PlantedDate.Should().Be(new DateOnly(2026, 5, 1));
    }

    [Fact]
    public void TheClearReasonFields_BindFromCamelCaseKeys()
    {
        var command = Bind(
            """{"plantedDate":null,"clearReason":"cropFailed","clearReasonNote":"washed out"}""");

        command.PlantedDateSpecified.Should().BeTrue();
        command.ClearReason.Should().Be("cropFailed");
        command.ClearReasonNote.Should().Be("washed out");
    }

    [Fact]
    public void AClearReasonIsAbsentWhenTheKeyIs()
    {
        var command = Bind("""{"plantedDate":null}""");

        command.ClearReason.Should().BeNull(
            "a missing reason must reach the handler as null so it can answer clear_reason_required");
        command.ClearReasonNote.Should().BeNull();
    }

    [Fact]
    public void TheClearReasonValueSurvivesBindingVerbatim_EvenWhenMisCased()
    {
        // ASP.NET's binder is case-insensitive about KEY names, and there is nothing it can do about VALUES.
        // "CROPFAILED" therefore arrives intact and is rejected by the handler as invalid_clear_reason —
        // which is the point: if binding ever normalised the value, a mis-cased client would silently "work"
        // here and break against any stricter reader.
        var command = Bind("""{"plantedDate":null,"ClearReason":"CROPFAILED"}""");

        command.ClearReason.Should().Be("CROPFAILED");
        PlantedDateRemovalReasons.TryParse(command.ClearReason).Should().BeNull();
    }

    // The mechanism itself, so the reason the tests above pass cannot be quietly removed.

    [Fact]
    public void ThePlantingDateIsNotAnAutoProperty_AndTheFlagIsWriteProtected()
    {
        var flag = typeof(UpdateWatchlistEntryCommand).GetProperty(
            nameof(UpdateWatchlistEntryCommand.PlantedDateSpecified))!;

        // Assert.NotNull rather than Should().NotBeNull(): FluentAssertions 8 binds a nullable-annotated
        // reference like Attribute? to its enum overload (CS0453).
        Assert.NotNull(flag.GetCustomAttribute<JsonIgnoreAttribute>());
        flag.SetMethod!.IsPublic.Should().BeFalse("only the PlantedDate setter may raise it");

        // An auto-property's backing field is named "<PlantedDate>k__BackingField"; the tri-state needs a
        // hand-written setter over a plain field, so that compiler-generated name must NOT be there.
        typeof(UpdateWatchlistEntryCommand)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Select(f => f.Name)
            .Should().NotContain("<PlantedDate>k__BackingField",
                "an auto-property cannot tell an omitted key from an explicit null");
    }

    // The SALES bodies, bound the same way. The contract here is the opposite of the watchlist's: PUT is a
    // FULL REPLACE, so an absent optional key must bind to null and CLEAR the stored value — there is no
    // tri-state and there must not be one, or "remove the market from this sale" becomes inexpressible.

    private static RecordSaleCommand BindPost(string json)
        => JsonSerializer.Deserialize<RecordSaleCommand>(json, Web)!;

    private static UpdateSaleCommand BindPut(string json)
        => JsonSerializer.Deserialize<UpdateSaleCommand>(json, Web)!;

    [Fact]
    public void ASaleBody_BindsFromCamelCaseKeys()
    {
        var command = BindPost(
            """
            {"cropId":"c0000000-0000-0000-0000-000000000001",
             "marketId":"b2a20001-0000-0000-0000-000000000001",
             "saleDate":"2026-07-28","pricePerKg":155.50,"quantityKg":42.5,"note":"paid in cash"}
            """);

        command.CropId.Should().Be(Guid.Parse("c0000000-0000-0000-0000-000000000001"));
        command.MarketId.Should().Be(Guid.Parse("b2a20001-0000-0000-0000-000000000001"));
        command.SaleDate.Should().Be("2026-07-28");
        command.PricePerKg.Should().Be(155.50m);
        command.QuantityKg.Should().Be(42.5m);
        command.Note.Should().Be("paid in cash");
    }

    [Fact]
    public void AnEmptySaleBody_LeavesEveryOptionalNull_AndThePriceNull()
    {
        var command = BindPost("{}");

        command.MarketId.Should().BeNull();
        command.QuantityKg.Should().BeNull();
        command.Note.Should().BeNull();

        // decimal? rather than decimal, so a missing price is answered with invalid_price rather than
        // silently becoming 0 and failing a different rule (or, worse, being stored).
        command.PricePerKg.Should().BeNull(
            "a missing price must reach the handler as absent, not as zero");
        command.SaleDate.Should().BeNull();
    }

    [Fact]
    public void TheSaleDateIsAString_SoEveryBadSpellingReachesTheHandler()
    {
        // A DateOnly-typed property would make "28/07/2026" a serializer error with a body the UI cannot
        // switch on; as a string it arrives intact and is answered with invalid_sale_date.
        typeof(RecordSaleCommand).GetProperty(nameof(RecordSaleCommand.SaleDate))!
            .PropertyType.Should().Be<string>();

        BindPost("""{"saleDate":"28/07/2026"}""").SaleDate.Should().Be("28/07/2026");
        PortfolioTime.ParseYmd("28/07/2026").Should().BeNull();
        PortfolioTime.ParseYmd("2026-07-28").Should().Be(new DateOnly(2026, 7, 28));
    }

    [Fact]
    public void OnAPut_AnAbsentOptionalKeyBindsToNull_WhichIsHowAValueIsCLEARED()
    {
        var command = BindPut("""{"saleDate":"2026-07-28","pricePerKg":155}""");

        command.MarketId.Should().BeNull();
        command.QuantityKg.Should().BeNull();
        command.Note.Should().BeNull();

        // ...and an explicit null is the SAME request. On this endpoint omission and null mean one thing,
        // deliberately unlike the watchlist PUT.
        var explicitNulls = BindPut(
            """{"saleDate":"2026-07-28","pricePerKg":155,"marketId":null,"quantityKg":null,"note":null}""");

        explicitNulls.MarketId.Should().BeNull();
        explicitNulls.QuantityKg.Should().BeNull();
        explicitNulls.Note.Should().BeNull();
    }

    [Fact]
    public void ThePutBodyCannotNameACrop()
    {
        // THE ENFORCEMENT IS THE SHAPE: an unknown JSON key is ignored, so a body carrying cropId binds to
        // a command that has nowhere to put it and the stored crop is untouched.
        typeof(UpdateSaleCommand).GetProperties().Select(p => p.Name).Should().NotContain("CropId");

        var command = BindPut(
            """{"cropId":"c0000000-0000-0000-0000-000000000009","saleDate":"2026-07-28","pricePerKg":155}""");

        command.SaleId.Should().Be(Guid.Empty, "the id comes from the route, never the body");
        command.PricePerKg.Should().Be(155m);
    }

    [Fact]
    public void TheOwnerCannotBeForgedFromTheWire()
    {
        // UserId IS a settable property (the controller stamps it), so the guarantee is not the type — it
        // is that the controller overwrites whatever arrived. This pins the shape the controller test then
        // proves is overwritten.
        BindPost("""{"userId":"22222222-2222-2222-2222-222222222222"}""")
            .UserId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"),
                "binding does not protect it — PortfolioControllerTests proves the JWT always wins");
    }

    [Fact]
    public void TheApiDoesNotUseNewtonsoftJson()
    {
        // The tri-state depends on System.Text.Json calling the property setter for a present-but-null key
        // and honouring [JsonIgnore]. A Newtonsoft input formatter reads different attributes entirely, so
        // adding one would silently change what a null plantedDate means.
        typeof(PortfolioController).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Should().NotContain(
                n => n != null && n.Contains("NewtonsoftJson", StringComparison.OrdinalIgnoreCase));
    }
}
