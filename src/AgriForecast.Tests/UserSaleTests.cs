using AgriForecast.Application.Requests.Admin.Logs.Common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.Migrations;
using AgriForecast.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// The farmer's sales log at rest: the <see cref="UserSale"/> entity, its EF mapping and migration, the
/// audit note it renders, and the activity-log event types.
/// <para>
/// The load-bearing claims this file guards are the QUARANTINE (PRD 3.1) and the PRIVACY OF THE NOTE.
/// Nothing may navigate from Users, Crops or Markets into a farmer's sales, and the farmer's own free text
/// must be UNREPRESENTABLE in an admin audit line — enforced here by a rendering signature that cannot
/// accept it, so widening that signature fails a test rather than leaking quietly.
/// </para>
/// Style mirrors PlantedDateRemovalTests (EF-model assertions against the SQL Server provider, never
/// connected; SQLite for the repository).
/// </summary>
public class UserSaleTests
{
    private static readonly Guid Farmer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Crop = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Market = Guid.Parse("b2a20001-0000-0000-0000-000000000001");
    private static readonly DateOnly Sold = new(2026, 7, 28);
    private static readonly DateTime Created = new(2026, 7, 30, 6, 0, 0, DateTimeKind.Utc);

    private static UserSale Record(
        Guid? marketId = null, decimal price = 155.00m, decimal? quantity = null, string? note = null)
        => UserSale.Record(Farmer, Crop, marketId, Sold, price, quantity, note, Created);

    // The entity factory.

    [Fact]
    public void Record_KeepsEveryFieldItWasGiven()
    {
        var row = Record(Market, 155.00m, 42.5m, "sold to the co-op");

        row.Id.Should().NotBe(Guid.Empty, "the entity assigns its own id, like UserCropWatchlist");
        row.UserId.Should().Be(Farmer);
        row.CropId.Should().Be(Crop);
        row.MarketId.Should().Be(Market);
        row.SaleDate.Should().Be(Sold);
        row.PricePerKg.Should().Be(155.00m);
        row.QuantityKg.Should().Be(42.5m);
        row.Note.Should().Be("sold to the co-op");
        row.CreatedAtUtc.Should().Be(Created);
        row.UpdatedAtUtc.Should().Be(Created, "an unedited row was last touched when it was created");
    }

    [Fact]
    public void Record_WithoutAMarketOrQuantity_IsAPerfectlyValidSale()
    {
        // "I sold it, I am not saying where, and I do not remember the weight" is an ordinary answer.
        // Demanding either would cost us the price too, which is the field that matters.
        var row = Record();

        row.MarketId.Should().BeNull();
        row.QuantityKg.Should().BeNull();
        row.Note.Should().BeNull();
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "c0000000-0000-0000-0000-000000000001", "userId")]
    [InlineData("11111111-1111-1111-1111-111111111111", "00000000-0000-0000-0000-000000000000", "cropId")]
    public void Record_RequiresRealIds(string userId, string cropId, string parameterName)
    {
        var act = () => UserSale.Record(
            Guid.Parse(userId), Guid.Parse(cropId), null, Sold, 155m, null, null, Created);

        act.Should().Throw<ArgumentException>().WithParameterName(parameterName);
    }

    [Fact]
    public void Record_RefusesAnAllZerosMarketId()
    {
        // Guid.Empty is not "no market" — it is a caller who lost a real id somewhere. Omitting the market
        // is spelled null, and the two must not silently become the same thing.
        var act = () => UserSale.Record(
            Farmer, Crop, Guid.Empty, Sold, 155m, null, null, Created);

        act.Should().Throw<ArgumentException>().WithParameterName("marketId");
    }

    [Fact]
    public void Record_RefusesADefaultSaleDate()
    {
        // An unset DateOnly is 0001-01-01. A sale with no plausible day cannot be compared to anything,
        // which is the only reason the row exists.
        var act = () => UserSale.Record(Farmer, Crop, null, default, 155m, null, null, Created);

        act.Should().Throw<ArgumentException>().WithParameterName("saleDate");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-155.5)]
    public void Record_RefusesANonPositivePrice(decimal price)
    {
        var act = () => UserSale.Record(Farmer, Crop, null, Sold, price, null, null, Created);

        act.Should().Throw<ArgumentException>().WithParameterName("pricePerKg");
    }

    [Fact]
    public void Record_RefusesAPriceAboveTheCeiling_ButAcceptsTheCeilingItself()
    {
        // Both sides of the boundary, because a cap tested from one side only is a cap nobody knows the
        // inclusivity of.
        Record(price: SaleLimits.MaxPricePerKg).PricePerKg.Should().Be(SaleLimits.MaxPricePerKg,
            "the ceiling is INCLUSIVE");

        var act = () => Record(price: SaleLimits.MaxPricePerKg + 0.01m);
        act.Should().Throw<ArgumentException>().WithParameterName("pricePerKg");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Record_RefusesANonPositiveQuantity_WhenOneIsSupplied(decimal quantity)
    {
        // Quantity is optional, but "0 kg" is not a sale — an unknown weight is spelled null.
        var act = () => Record(quantity: quantity);

        act.Should().Throw<ArgumentException>().WithParameterName("quantityKg");
    }

    [Fact]
    public void Record_RefusesAQuantityAboveTheCeiling_ButAcceptsTheCeilingItself()
    {
        Record(quantity: SaleLimits.MaxQuantityKg).QuantityKg.Should().Be(SaleLimits.MaxQuantityKg);

        var act = () => Record(quantity: SaleLimits.MaxQuantityKg + 0.01m);
        act.Should().Throw<ArgumentException>().WithParameterName("quantityKg");
    }

    [Fact]
    public void Record_RequiresAClock()
    {
        var act = () => UserSale.Record(Farmer, Crop, null, Sold, 155m, null, null, default);

        act.Should().Throw<ArgumentException>().WithParameterName("createdAtUtc");
    }

    [Fact]
    public void Record_TrimsTheNote()
    {
        Record(note: "   paid in cash \n").Note.Should().Be("paid in cash");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_BlankNote_StoresNull_NotAnEmptyString(string? note)
    {
        Record(note: note).Note.Should().BeNull(
            "an empty string would read as 'the farmer wrote something' when they wrote nothing");
    }

    [Fact]
    public void Record_OverlongNote_IsCappedToTheColumnLength()
    {
        // Defence in depth only: the handler answers an over-long note with note_too_long, so this
        // truncation should never be reached in production. It exists so a future caller cannot overflow
        // the column.
        var row = Record(note: new string('x', UserSale.NoteMaxLength + 50));

        row.Note.Should().HaveLength(UserSale.NoteMaxLength);
        UserSale.NoteMaxLength.Should().Be(500, "the cap is the wire contract and the column");
    }

    // Revise: the PUT path.

    [Fact]
    public void Revise_ReplacesEveryMutableField_AndStampsUpdatedAt()
    {
        var row = Record(Market, 155m, 42.5m, "first note");
        var later = Created.AddDays(2);

        var changed = row.Revise(null, new DateOnly(2026, 7, 20), 90m, 10m, "second note", later);

        changed.Should().BeTrue();
        row.MarketId.Should().BeNull("a null market on a PUT CLEARS it — this is a full replace");
        row.SaleDate.Should().Be(new DateOnly(2026, 7, 20));
        row.PricePerKg.Should().Be(90m);
        row.QuantityKg.Should().Be(10m);
        row.Note.Should().Be("second note");
        row.UpdatedAtUtc.Should().Be(later);
        row.CreatedAtUtc.Should().Be(Created, "an edit never rewrites when the row was first recorded");
    }

    [Fact]
    public void Revise_NullsClearTheOptionalFields()
    {
        var row = Record(Market, 155m, 42.5m, "a note");

        row.Revise(null, Sold, 155m, null, null, Created.AddDays(1));

        row.MarketId.Should().BeNull();
        row.QuantityKg.Should().BeNull();
        row.Note.Should().BeNull();
    }

    [Fact]
    public void Revise_ANoOpEdit_ReturnsFalse_AndDoesNotChurnUpdatedAt()
    {
        var row = Record(Market, 155m, 42.5m, "a note");

        var changed = row.Revise(Market, Sold, 155m, 42.5m, "a note", Created.AddDays(5));

        changed.Should().BeFalse();
        row.UpdatedAtUtc.Should().Be(Created,
            "a save that changed nothing must not make the row look edited");
    }

    [Fact]
    public void Revise_ATrailingSpaceOnAnUnchangedNote_IsStillANoOp()
    {
        // The note is compared AFTER trimming, because trimming is what would be stored. Otherwise a
        // stray space would make every save look like an edit.
        var row = Record(Market, 155m, null, "a note");

        row.Revise(Market, Sold, 155m, null, "  a note  ", Created.AddDays(5)).Should().BeFalse();
    }

    [Fact]
    public void Revise_AppliesTheSameGuardsAsRecord()
    {
        var row = Record(Market, 155m, null, null);

        var badPrice = () => row.Revise(Market, Sold, 0m, null, null, Created.AddDays(1));
        badPrice.Should().Throw<ArgumentException>().WithParameterName("pricePerKg");

        var overCeiling = () => row.Revise(
            Market, Sold, SaleLimits.MaxPricePerKg + 1m, null, null, Created.AddDays(1));
        overCeiling.Should().Throw<ArgumentException>().WithParameterName("pricePerKg");

        var badQuantity = () => row.Revise(Market, Sold, 155m, 0m, null, Created.AddDays(1));
        badQuantity.Should().Throw<ArgumentException>().WithParameterName("quantityKg");

        var badDate = () => row.Revise(Market, default, 155m, null, null, Created.AddDays(1));
        badDate.Should().Throw<ArgumentException>().WithParameterName("saleDate");

        var noClock = () => row.Revise(Market, Sold, 155m, null, null, default);
        noClock.Should().Throw<ArgumentException>().WithParameterName("updatedAtUtc");

        // Every one of those threw BEFORE anything was written.
        row.PricePerKg.Should().Be(155m);
        row.SaleDate.Should().Be(Sold);
        row.UpdatedAtUtc.Should().Be(Created);
    }

    [Fact]
    public void TheCropCannotBeChanged_BecauseNoMethodTakesOne()
    {
        // THE ENFORCEMENT IS THE SIGNATURE. A sale recorded against the wrong crop is deleted and re-added;
        // re-pointing one would silently re-attribute a reported price to a crop the farmer never named.
        // Record() is the only member allowed to accept a crop id.
        var offenders = typeof(UserSale).GetMethods()
            .Where(m => m.DeclaringType == typeof(UserSale)
                        && m.Name != nameof(UserSale.Record)
                        && m.GetParameters().Any(p => p.Name is "cropId"))
            .Select(m => m.Name)
            .ToList();

        offenders.Should().BeEmpty("only the factory may set the crop; nothing may re-point it");

        typeof(UserSale).GetProperties()
            .Where(p => p.SetMethod != null && p.SetMethod.IsPublic)
            .Select(p => p.Name)
            .Should().BeEmpty("every setter is private so the factory and Revise are the only ways in");
    }

    // The audit note.

    [Fact]
    public void RenderAuditDetails_IsCropCodeThenPriceThenDate()
    {
        SaleAuditDetails.RenderAuditDetails("VEG000041", 155m, new DateOnly(2026, 7, 28))
            .Should().Be("VEG000041 · Rs 155.00/kg · 2026-07-28");
    }

    [Fact]
    public void RenderAuditDetails_AlwaysUsesTwoDecimalsAndAnIsoDate()
    {
        // Invariant formatting, so an admin's server locale can never re-punctuate a stored audit line.
        SaleAuditDetails.RenderAuditDetails("VEG000041", 7.5m, new DateOnly(2026, 1, 5))
            .Should().Be("VEG000041 · Rs 7.50/kg · 2026-01-05");
        SaleAuditDetails.RenderAuditDetails("VEG000041", 1234.567m, new DateOnly(2026, 12, 31))
            .Should().Be("VEG000041 · Rs 1234.57/kg · 2026-12-31");
    }

    [Fact]
    public void RenderAuditDetails_BlankCropCode_DropsTheCodeAndItsSeparator()
    {
        // The crop code comes from a read-back, which can (rarely) fail. A note starting with " · " would
        // look like a rendering bug; the price and date alone are honest and still useful.
        SaleAuditDetails.RenderAuditDetails(null, 155m, Sold).Should().Be("Rs 155.00/kg · 2026-07-28");
        SaleAuditDetails.RenderAuditDetails("   ", 155m, Sold).Should().Be("Rs 155.00/kg · 2026-07-28");
    }

    [Fact]
    public void RenderAuditDetails_CannotBeGivenTheFarmersNote()
    {
        // THE ENFORCEMENT IS THE SIGNATURE, exactly as for PlantedDateRemovalReasons.RenderAuditDetails.
        // Audit Details is code-authored text only; the note lives solely on the UserSales row. A renderer
        // that COULD take the note would leak it the first time somebody passed the wrong local variable at
        // a call site, so widening this signature must fail here.
        var parameters = typeof(SaleAuditDetails)
            .GetMethod(nameof(SaleAuditDetails.RenderAuditDetails))!
            .GetParameters();

        parameters.Select(p => p.ParameterType).Should().Equal(
            typeof(string), typeof(decimal), typeof(DateOnly));
        parameters.Select(p => p.Name).Should().Equal("cropCode", "pricePerKg", "saleDate");

        // ...and there is no OTHER public renderer on the type that a caller could reach for instead.
        typeof(SaleAuditDetails).GetMethods()
            .Where(m => m.DeclaringType == typeof(SaleAuditDetails) && m.IsPublic)
            .Select(m => m.Name)
            .Should().Equal(nameof(SaleAuditDetails.RenderAuditDetails));
    }

    [Fact]
    public void NoAuditSeamMethodCanCarryTheNote()
    {
        // The other half of the same law: the audit INTERFACE takes a pre-rendered string, so there is no
        // sale-shaped overload somebody could hand a whole UserSale (note included) to.
        var saleMethods = typeof(Application.Services.IUserActivityAudit).GetMethods()
            .Where(m => m.Name.Contains("Sale", StringComparison.Ordinal))
            .ToList();

        saleMethods.Select(m => m.Name).Should().BeEquivalentTo(
            "RecordSaleRecordedAsync", "RecordSaleUpdatedAsync", "RecordSaleDeletedAsync");

        foreach (var method in saleMethods)
            method.GetParameters().Select(p => p.ParameterType).Should().Equal(
                new[] { typeof(Guid), typeof(string), typeof(CancellationToken) },
                $"{method.Name} must take an actor and a PRE-RENDERED note, never an entity");
    }

    // The activity-log event types.

    [Theory]
    [InlineData(UserActivityEventType.SaleRecorded, 13, "saleRecorded")]
    [InlineData(UserActivityEventType.SaleUpdated, 14, "saleUpdated")]
    [InlineData(UserActivityEventType.SaleDeleted, 15, "saleDeleted")]
    public void ActivityEvents_WireStringsAndNumbersArePinned(
        UserActivityEventType type, int expectedValue, string expectedWire)
    {
        ((int)type).Should().Be(expectedValue,
            "appended after PlantedDateRemoved; the column stores the int, so never renumber");
        UserActivityEventStrings.ToWire(type).Should().Be(expectedWire);
        UserActivityEventStrings.KnownTypes.Should().Contain(expectedWire,
            "the admin Logs page must be able to FILTER for it, not just render it");
        UserActivityEventStrings.TryParse(expectedWire).Should().Be(type);
    }

    [Theory]
    [InlineData(UserActivityEventType.LoginSucceeded, 0)]
    [InlineData(UserActivityEventType.LoginFailed, 1)]
    [InlineData(UserActivityEventType.UserRegistered, 2)]
    [InlineData(UserActivityEventType.RoleChanged, 3)]
    [InlineData(UserActivityEventType.UserDeleted, 4)]
    [InlineData(UserActivityEventType.PolicyFlagChanged, 5)]
    [InlineData(UserActivityEventType.FestivalChanged, 6)]
    [InlineData(UserActivityEventType.NewsEventChanged, 7)]
    [InlineData(UserActivityEventType.CropChanged, 8)]
    [InlineData(UserActivityEventType.MarketChanged, 9)]
    [InlineData(UserActivityEventType.IngestionServiceStarted, 10)]
    [InlineData(UserActivityEventType.IngestionServiceStopRequested, 11)]
    [InlineData(UserActivityEventType.PlantedDateRemoved, 12)]
    public void TheTwelveExistingEventValues_AreUntouched(UserActivityEventType type, int expected)
    {
        // Appending three members must not have shifted anything: every value below is already written into
        // UserActivityLog rows in the live database, and a renumber would silently re-label all of them.
        ((int)type).Should().Be(expected);
    }

    [Fact]
    public void ActivityEvents_CarryTheFarmerAsActor_WithNoTargetOrUsername()
    {
        var details = SaleAuditDetails.RenderAuditDetails("VEG000041", 155m, Sold);

        foreach (var row in new[]
                 {
                     UserActivityEvent.SaleRecorded(Farmer, details, Created),
                     UserActivityEvent.SaleUpdated(Farmer, details, Created),
                     UserActivityEvent.SaleDeleted(Farmer, details, Created)
                 })
        {
            row.ActorUserId.Should().Be(Farmer, "a farmer's sale is authored by the farmer, not an admin");
            row.TargetUserId.Should().BeNull("a sale acts on the farmer's own data, not on another user");
            row.UsernameAttempted.Should().BeNull();
            row.Details.Should().Be(details);
            row.OccurredUtc.Should().Be(Created);
        }
    }

    [Fact]
    public async Task ActivityEvents_WriteFailure_IsSwallowed_LikeEveryOtherAuditCall()
    {
        // No UserActivityLog table => the write throws inside UserActivityAudit and must be swallowed there.
        // The SALE is committed and load-bearing; this LOG LINE is not, and a farmer's sale must not fail
        // because an admin log could not be written.
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _c = connection;

        var services = new ServiceCollection();
        services.AddDbContext<AgriForecastDbContext>(o => o.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();

        var audit = new UserActivityAudit(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<UserActivityAudit>.Instance);

        var record = () => audit.RecordSaleRecordedAsync(Farmer, "VEG000041 · Rs 155.00/kg · 2026-07-28");
        var update = () => audit.RecordSaleUpdatedAsync(Farmer, "VEG000041 · Rs 155.00/kg · 2026-07-28");
        var delete = () => audit.RecordSaleDeletedAsync(Farmer, "VEG000041 · Rs 155.00/kg · 2026-07-28");

        await record.Should().NotThrowAsync();
        await update.Should().NotThrowAsync();
        await delete.Should().NotThrowAsync();
    }

    // EF mapping. The model is built against the SQL Server provider (never connected) so the assertions are
    // about the real production mapping, not a test-only approximation.

    private static IEntityType SaleEntityType()
    {
        var options = new DbContextOptionsBuilder<AgriForecastDbContext>()
            .UseSqlServer("Server=(model-only);Database=none;")
            .Options;
        using var ctx = new AgriForecastDbContext(options);
        return ctx.Model.FindEntityType(typeof(UserSale))!;
    }

    [Fact]
    public void Mapping_UsesThePluralTableName()
    {
        SaleEntityType().GetTableName().Should().Be("UserSales");
    }

    [Fact]
    public void Mapping_StoresTheSaleDateAsADateWithNoHiddenTime()
    {
        var property = SaleEntityType().FindProperty("SaleDate")!;

        property.IsNullable.Should().BeFalse("a sale with no day cannot be compared to anything");
        property.GetColumnType().Should().Be("date",
            "a sale happened on a DAY; a hidden 00:00:00 makes it timezone-dependent");
    }

    [Fact]
    public void Mapping_GivesTheMoneyColumnsExplicitPrecision()
    {
        var price = SaleEntityType().FindProperty("PricePerKg")!;
        price.IsNullable.Should().BeFalse("the price is the one thing a sale cannot omit");
        price.GetPrecision().Should().Be(10);
        price.GetScale().Should().Be(2);

        var quantity = SaleEntityType().FindProperty("QuantityKg")!;
        quantity.IsNullable.Should().BeTrue("plenty of farmers remember the price and not the weight");
        quantity.GetPrecision().Should().Be(12);
        quantity.GetScale().Should().Be(2);
    }

    [Fact]
    public void Mapping_CapsTheNote_AndKeepsItOptional()
    {
        var note = SaleEntityType().FindProperty("Note")!;

        note.IsNullable.Should().BeTrue();
        note.GetMaxLength().Should().Be(UserSale.NoteMaxLength);
    }

    [Fact]
    public void Mapping_KeepsTheMarketOptional()
    {
        SaleEntityType().FindProperty("MarketId")!.IsNullable.Should().BeTrue(
            "'I sold it, I am not saying where' is a normal answer, not missing data");
    }

    [Theory]
    // Users CASCADE, matching UserCropWatchlist: personal data does not outlive its owner.
    [InlineData("UserId", DeleteBehavior.Cascade)]
    // Crops and Markets RESTRICT: reference data cannot be deleted out from under a farmer's own record.
    [InlineData("CropId", DeleteBehavior.Restrict)]
    [InlineData("MarketId", DeleteBehavior.Restrict)]
    public void Mapping_UsesTheIntendedDeleteBehaviourPerForeignKey(string property, DeleteBehavior expected)
    {
        SaleEntityType().GetForeignKeys()
            .Single(f => f.Properties.Single().Name == property)
            .DeleteBehavior.Should().Be(expected);
    }

    [Fact]
    public void Mapping_GivesUserCropAndMarketNoNavigationIntoTheSales()
    {
        // THE QUARANTINE (PRD 3.1). If Crop or Market ever gained a collection of sales, a careless Include
        // on a reference-data query would drag one farmer's prices into another farmer's response — and,
        // worse, put them one property access away from the feature layer.
        foreach (var fk in SaleEntityType().GetForeignKeys())
            Assert.True(
                fk.PrincipalToDependent is null,
                $"{fk.PrincipalEntityType.ClrType.Name} must gain no navigation into farmer sales data");
    }

    [Fact]
    public void Mapping_TheSaleItselfHasNoNavigationsEither()
    {
        // The arrow points nowhere in either direction: the entity holds ids, not references. A navigation
        // FROM the sale would be a second path by which an unrelated query could materialise one.
        SaleEntityType().GetNavigations().Should().BeEmpty();
    }

    [Fact]
    public void Mapping_IndexesTheFarmersSalesNewestFirst()
    {
        // The DESIGN-TIME model: IsDescending is not kept in the read-optimized runtime model and throws
        // there (same reason PlantedDateRemovalTests reads its index this way).
        var options = new DbContextOptionsBuilder<AgriForecastDbContext>()
            .UseSqlServer("Server=(model-only);Database=none;")
            .Options;
        using var ctx = new AgriForecastDbContext(options);

        var index = ctx.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(UserSale))!
            .GetIndexes()
            .Single(i => i.Name == "IX_UserSales_UserSaleDate");

        index.Properties.Select(p => p.Name).Should().Equal("UserId", "SaleDate");
        index.IsUnique.Should().BeFalse(
            "two sales of the same crop on the same day is an ordinary thing to have happened");
        index.IsDescending.Should().Equal(false, true);
    }

    // The migration.

    [Fact]
    public void Migration_CreatesTheTableWithTheIntendedShape()
    {
        var table = new CreateUserSales().UpOperations
            .OfType<CreateTableOperation>()
            .Single(c => c.Name == "UserSales");

        var columns = table.Columns.ToDictionary(c => c.Name);
        columns["SaleDate"].ColumnType.Should().Be("date");
        columns["PricePerKg"].ColumnType.Should().Be("decimal(10,2)");
        columns["QuantityKg"].ColumnType.Should().Be("decimal(12,2)");
        columns["Note"].MaxLength.Should().Be(UserSale.NoteMaxLength);

        // Everything that makes the row a record is NOT NULL; everything the farmer may decline to say is
        // nullable.
        foreach (var required in new[] { "Id", "UserId", "CropId", "SaleDate", "PricePerKg" })
            columns[required].IsNullable.Should().BeFalse($"{required} is what makes the row a record");

        foreach (var optional in new[] { "MarketId", "QuantityKg", "Note" })
            columns[optional].IsNullable.Should().BeTrue($"{optional} is the farmer's to decline");

        table.ForeignKeys.Single(f => f.PrincipalTable == "Users")
            .OnDelete.Should().Be(ReferentialAction.Cascade);
        table.ForeignKeys.Single(f => f.PrincipalTable == "Crops")
            .OnDelete.Should().Be(ReferentialAction.Restrict);
        table.ForeignKeys.Single(f => f.PrincipalTable == "Markets")
            .OnDelete.Should().Be(ReferentialAction.Restrict);
    }

    [Fact]
    public void Migration_CarriesNoBackfill()
    {
        // The table is NET-NEW: a sales log starts empty because nobody had anywhere to record a sale before
        // it existed, and inventing rows would be fabricating a farmer's history.
        new CreateUserSales().UpOperations.OfType<SqlOperation>().Should().BeEmpty();
    }

    [Fact]
    public void Migration_TouchesNothingButItsOwnTable()
    {
        // A quarantined table must arrive without so much as a column added to a table the ML side reads.
        new CreateUserSales().UpOperations
            .Select(op => op switch
            {
                CreateTableOperation c => c.Name,
                CreateIndexOperation i => i.Table,
                _ => "OTHER:" + op.GetType().Name
            })
            .Distinct()
            .Should().Equal("UserSales");
    }

    // The repository, against a real (SQLite) database — the write must reach the table and be flushed by
    // the CALLER's SaveChanges, and the load must be user-scoped.

    private const string CreateUserSalesTableSql = """
        CREATE TABLE "UserSales" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_UserSales" PRIMARY KEY,
            "UserId" TEXT NOT NULL,
            "CropId" TEXT NOT NULL,
            "MarketId" TEXT NULL,
            "SaleDate" TEXT NOT NULL,
            "PricePerKg" TEXT NOT NULL,
            "QuantityKg" TEXT NULL,
            "Note" TEXT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL
        );
        """;

    private static async Task<AgriForecastDbContext> SalesDbAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AgriForecastDbContext>()
            .UseSqlite(connection)
            .Options;

        var ctx = new AgriForecastDbContext(options);
        await ctx.Database.ExecuteSqlRawAsync(CreateUserSalesTableSql);
        return ctx;
    }

    [Fact]
    public async Task Repository_AddsWithoutSaving_SoTheCallersCommitIsTheOnlyWrite()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _c = connection;

        await using var ctx = await SalesDbAsync(connection);

        var repository = new UserSaleRepository(ctx);
        await repository.AddAsync(Record(Market, 155m, 42.5m, "kept whole"));

        ctx.UserSales.AsNoTracking().Should().BeEmpty(
            "AddAsync only tracks: the row lands when the handler's unit of work commits");

        await ctx.SaveChangesAsync();

        var row = ctx.UserSales.AsNoTracking().Single();
        row.PricePerKg.Should().Be(155m);
        row.QuantityKg.Should().Be(42.5m);
        row.SaleDate.Should().Be(Sold);
        row.Note.Should().Be("kept whole");
    }

    // The READ STORE, against a real (SQLite) database.
    //
    // THESE TESTS EXIST BECAUSE OF A REAL 500. The first cut of the sales projection joined Crops and
    // Markets and then sorted the projected record; EF Core could translate neither (a GroupJoin needs
    // matching key types, so the optional market forced a Guid->Guid? cast into an untranslatable object
    // comparison, and an ORDER BY over a constructor call is not a thing). Every unit test passed, because
    // an in-memory fake store answers LINQ that no database ever sees. Only executing the real query finds
    // it — hence a real database here, minimal though it is.
    //
    // Only the columns the projection reads are created: EF selects exactly those, so a full model would
    // add maintenance without adding coverage. EnsureCreated is avoided for the reason the watchlist tests
    // give — the full model's ISJSON constraints and SYSUTCDATETIME() defaults are not SQLite.
    private const string CreateReferenceTablesSql = """
        CREATE TABLE "Crops" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_Crops" PRIMARY KEY,
            "Name" TEXT NOT NULL,
            "CropCode" TEXT NULL
        );
        CREATE TABLE "Markets" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_Markets" PRIMARY KEY,
            "Name" TEXT NOT NULL,
            "ShortCode" TEXT NOT NULL
        );
        """;

    private static async Task<AgriForecastDbContext> SalesDbWithReferenceDataAsync(
        SqliteConnection connection)
    {
        var ctx = await SalesDbAsync(connection);
        await ctx.Database.ExecuteSqlRawAsync(CreateReferenceTablesSql);

        // PARAMETERISED, not string-interpolated into the SQL text: the ids must be written through the
        // SAME provider type mapping EF reads them back with. A hand-formatted GUID literal is compared
        // case-sensitively by SQLite and silently matches nothing, which looks exactly like a broken join.
        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Crops" ("Id", "Name", "CropCode") VALUES ({Crop}, 'Carrot', 'VEG000001');""");

        await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "Markets" ("Id", "Name", "ShortCode")
             VALUES ({Market}, 'Dambulla Dedicated Economic Centre', 'DEC');
             """);

        return ctx;
    }

    [Fact]
    public async Task ReadStore_ProjectsTheJoinedDisplayFields_AndTranslatesToRealSql()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _c = connection;

        await using var ctx = await SalesDbWithReferenceDataAsync(connection);

        var withMarket = Record(Market, 155m, 42.5m, "paid in cash");
        var withoutMarket = UserSale.Record(
            Farmer, Crop, null, Sold.AddDays(-1), 90m, null, null, Created.AddMinutes(1));

        await ctx.UserSales.AddRangeAsync(withMarket, withoutMarket);
        await ctx.SaveChangesAsync();

        var store = new Infrastructure.Services.PortfolioRead.PortfolioReadStore(ctx);

        var page = await store.GetSalesPageAsync(Farmer, null, 1, 20);

        page.Total.Should().Be(2);
        page.Items.Select(i => i.Id).Should().Equal(
            new[] { withMarket.Id, withoutMarket.Id }, "newest SaleDate first");

        var first = page.Items[0];
        first.CropName.Should().Be("Carrot");
        first.CropCode.Should().Be("VEG000001");
        first.MarketName.Should().Be("Dambulla Dedicated Economic Centre");
        first.MarketShortCode.Should().Be("DEC");
        first.PricePerKg.Should().Be(155m);
        first.Note.Should().Be("paid in cash");

        // The LEFT-join half: a sale with no market keeps its crop fields and nulls the market ones, rather
        // than dropping out of the list entirely (which an inner join would have done, silently).
        var second = page.Items[1];
        second.CropName.Should().Be("Carrot");
        second.MarketId.Should().BeNull();
        second.MarketName.Should().BeNull();
        second.MarketShortCode.Should().BeNull();
    }

    [Fact]
    public async Task ReadStore_PagesAndFiltersAndScopesToTheOwner()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _c = connection;

        await using var ctx = await SalesDbWithReferenceDataAsync(connection);

        var stranger = Guid.Parse("22222222-2222-2222-2222-222222222222");

        for (var i = 0; i < 3; i++)
            await ctx.UserSales.AddAsync(UserSale.Record(
                Farmer, Crop, null, Sold.AddDays(-i), 100m + i, null, null, Created.AddMinutes(i)));

        await ctx.UserSales.AddAsync(UserSale.Record(
            stranger, Crop, null, Sold, 999m, null, null, Created));

        await ctx.SaveChangesAsync();

        var store = new Infrastructure.Services.PortfolioRead.PortfolioReadStore(ctx);

        var firstPage = await store.GetSalesPageAsync(Farmer, null, 1, 2);
        firstPage.Items.Should().HaveCount(2);
        firstPage.Total.Should().Be(3, "the stranger's row is not the caller's business, or their total");
        firstPage.Items.Should().NotContain(i => i.PricePerKg == 999m);

        var secondPage = await store.GetSalesPageAsync(Farmer, null, 2, 2);
        secondPage.Items.Should().ContainSingle();

        var wrongCrop = await store.GetSalesPageAsync(Farmer, Guid.NewGuid(), 1, 20);
        wrongCrop.Items.Should().BeEmpty("an unknown crop filter matches nothing, it is not an error");
        wrongCrop.Total.Should().Be(0);
    }

    [Fact]
    public async Task ReadStore_GetSale_IsOwnerScoped()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _c = connection;

        await using var ctx = await SalesDbWithReferenceDataAsync(connection);

        var mine = Record(Market, 155m);
        var theirs = UserSale.Record(
            Guid.Parse("22222222-2222-2222-2222-222222222222"), Crop, null, Sold, 90m, null, null, Created);

        await ctx.UserSales.AddRangeAsync(mine, theirs);
        await ctx.SaveChangesAsync();

        var store = new Infrastructure.Services.PortfolioRead.PortfolioReadStore(ctx);

        Assert.NotNull(await store.GetSaleAsync(Farmer, mine.Id));

        // The other farmer's row, addressed by its real id, reads as absent — same answer an id that never
        // existed gets, which is what lets both become one 404.
        Assert.Null(await store.GetSaleAsync(Farmer, theirs.Id));
    }

    [Fact]
    public async Task Repository_LoadsOnlyTheCallersOwnRow()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _c = connection;

        await using var ctx = await SalesDbAsync(connection);

        var mine = Record(Market, 155m);
        var theirs = UserSale.Record(
            Guid.Parse("22222222-2222-2222-2222-222222222222"), Crop, null, Sold, 90m, null, null, Created);

        var repository = new UserSaleRepository(ctx);
        await repository.AddAsync(mine);
        await repository.AddAsync(theirs);
        await ctx.SaveChangesAsync();

        // Assert.NotNull / Assert.Null rather than Should(): FluentAssertions 8 binds a nullable-annotated
        // reference like UserSale? to its enum overload (CS0453).
        Assert.NotNull(await repository.GetForUserAsync(Farmer, mine.Id));

        // The other farmer's row, addressed by its real id, is simply not there for this caller — which is
        // what lets the handler answer the same 404 it gives an id that never existed. The user filter is
        // in the query, not left to the caller to remember.
        Assert.Null(await repository.GetForUserAsync(Farmer, theirs.Id));

        Assert.Null(await repository.GetForUserAsync(Farmer, Guid.NewGuid()));
    }
}
