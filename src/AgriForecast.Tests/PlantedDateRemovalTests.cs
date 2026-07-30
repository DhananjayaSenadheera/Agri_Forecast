using AgriForecast.Application.Requests.Admin.Logs.Common;
using AgriForecast.Application.Requests.Portfolio.Common;
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
/// The record a farmer's planting-date removal leaves behind: the <see cref="PlantedDateRemoval"/> entity,
/// its EF mapping and migration, the <c>clearReason</c> wire vocabulary, and the activity-log event.
/// <para>
/// The load-bearing distinction this file guards is that the REMOVAL ROW IS NOT AN AUDIT ROW. It is written
/// inside the same transaction as the clear (proved in PortfolioHandlerTests, where the commit count at
/// insert time is asserted), it carries the farmer's free-text note, and that note must never appear in
/// UserActivityLog.Details — which here is enforced by a rendering signature that cannot accept it.
/// </para>
/// Style mirrors UserCropWatchlistTests (EF-model assertions against the SQL Server provider, never
/// connected) and UserActivityAuditTests (SQLite for the writer).
/// </summary>
public class PlantedDateRemovalTests
{
    private static readonly Guid Farmer = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Crop = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly DateOnly Planted = new(2026, 5, 1);
    private static readonly DateTime Occurred = new(2026, 7, 30, 6, 0, 0, DateTimeKind.Utc);

    private static PlantedDateRemoval Record(
        string? note = null, PlantedDateRemovalReason reason = PlantedDateRemovalReason.Harvested)
        => PlantedDateRemoval.Record(Farmer, Crop, Planted, reason, note, Occurred);

    // The entity factory.

    [Fact]
    public void Record_KeepsEveryFieldItWasGiven()
    {
        var row = Record("bumper crop", PlantedDateRemovalReason.Harvested);

        row.UserId.Should().Be(Farmer);
        row.CropId.Should().Be(Crop);
        row.RemovedPlantedDate.Should().Be(Planted,
            "the cleared date is the only surviving trace of that planting once the column is null");
        row.Reason.Should().Be(PlantedDateRemovalReason.Harvested);
        row.Note.Should().Be("bumper crop");
        row.OccurredUtc.Should().Be(Occurred);
        row.Id.Should().Be(0, "Id is a store-generated bigint identity, never assigned in code");
    }

    [Fact]
    public void Record_TrimsTheNote()
    {
        Record("   sold the plot \n").Note.Should().Be("sold the plot");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Record_BlankNote_StoresNull_NotAnEmptyString(string? note)
    {
        Record(note).Note.Should().BeNull(
            "an empty string would read as 'the farmer wrote something' when they wrote nothing");
    }

    [Fact]
    public void Record_OverlongNote_IsCappedToTheColumnLength()
    {
        // Defence in depth only: the handler answers an over-long note with clear_reason_note_too_long, so
        // this truncation should never be reached in production. It exists so a future caller cannot
        // overflow the column.
        var row = Record(new string('x', PlantedDateRemoval.NoteMaxLength + 50));

        row.Note.Should().HaveLength(PlantedDateRemoval.NoteMaxLength);
        PlantedDateRemoval.NoteMaxLength.Should().Be(300, "the cap is the wire contract and the column");
    }

    [Fact]
    public void Record_NoteAtExactlyTheCap_IsKeptWhole()
    {
        var note = new string('x', PlantedDateRemoval.NoteMaxLength);

        Record(note).Note.Should().Be(note);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-000000000000", "c0000000-0000-0000-0000-000000000001", "userId")]
    [InlineData("11111111-1111-1111-1111-111111111111", "00000000-0000-0000-0000-000000000000", "cropId")]
    public void Record_RequiresRealIds(string userId, string cropId, string parameterName)
    {
        var act = () => PlantedDateRemoval.Record(
            Guid.Parse(userId), Guid.Parse(cropId), Planted,
            PlantedDateRemovalReason.Harvested, null, Occurred);

        act.Should().Throw<ArgumentException>().WithParameterName(parameterName);
    }

    [Fact]
    public void Record_RequiresAClock()
    {
        var act = () => PlantedDateRemoval.Record(
            Farmer, Crop, Planted, PlantedDateRemovalReason.Harvested, null, default);

        act.Should().Throw<ArgumentException>().WithParameterName("occurredUtc");
    }

    [Fact]
    public void Record_RefusesADefaultRemovedPlantedDate()
    {
        // An unset DateOnly is 0001-01-01. This column is the only surviving trace of the planting that was
        // cleared, so a row that says "year 1" records nothing at all — and "harvested" with no plausible
        // planting date is a half-truth of exactly the kind this table exists to prevent.
        var act = () => PlantedDateRemoval.Record(
            Farmer, Crop, default, PlantedDateRemovalReason.Harvested, null, Occurred);

        act.Should().Throw<ArgumentException>().WithParameterName("removedPlantedDate");
    }

    [Fact]
    public void Record_RefusesAnUndefinedReason()
    {
        // A cast int is how a reason would arrive from a future wire value nobody mapped. A row whose reason
        // means nothing is worse than a rejected write.
        var act = () => PlantedDateRemoval.Record(
            Farmer, Crop, Planted, (PlantedDateRemovalReason)99, null, Occurred);

        act.Should().Throw<ArgumentException>().WithParameterName("reason");
    }

    [Fact]
    public void TheEntityHasNoMutators_BecauseTheTableIsAppendOnly()
    {
        // Property accessors excluded (IsSpecialName) — what must not exist is a behaviour method: this
        // entity has no Mature(), no Update(), no Redact().
        typeof(PlantedDateRemoval).GetMethods()
            .Where(m => m.DeclaringType == typeof(PlantedDateRemoval) && !m.IsStatic && !m.IsSpecialName)
            .Select(m => m.Name)
            .Should().BeEmpty("nothing edits a removal after the fact — that is what append-only means");

        // A plain LINQ filter rather than OnlyContain: the predicate needs `is null`, which an expression
        // tree cannot hold.
        typeof(PlantedDateRemoval).GetProperties()
            .Where(p => p.SetMethod != null && p.SetMethod.IsPublic)
            .Select(p => p.Name)
            .Should().BeEmpty("every setter is private so the factory is the only way in");
    }

    // The reason vocabulary.

    [Theory]
    [InlineData(PlantedDateRemovalReason.Harvested, "harvested")]
    [InlineData(PlantedDateRemovalReason.CropFailed, "cropFailed")]
    [InlineData(PlantedDateRemovalReason.EnteredByMistake, "enteredByMistake")]
    [InlineData(PlantedDateRemovalReason.Other, "other")]
    public void ReasonStrings_ToWire_AreFrozenCamelCase(PlantedDateRemovalReason reason, string expected)
    {
        PlantedDateRemovalReasons.ToWire(reason).Should().Be(expected);
        PlantedDateRemovalReasons.TryParse(expected).Should().Be(reason, "the mapping must round-trip");
    }

    // Stored as int; a reorder would silently re-label every existing row (fix belongs in a migration).
    [Theory]
    [InlineData(PlantedDateRemovalReason.Harvested, 0)]
    [InlineData(PlantedDateRemovalReason.CropFailed, 1)]
    [InlineData(PlantedDateRemovalReason.EnteredByMistake, 2)]
    [InlineData(PlantedDateRemovalReason.Other, 3)]
    public void ReasonNumericValues_ArePinned(PlantedDateRemovalReason reason, int expected)
    {
        ((int)reason).Should().Be(expected);
    }

    [Fact]
    public void ReasonStrings_CoverEveryEnumMember()
    {
        // Same trap as UserActivityEventStrings: a reason that renders but does not parse (or vice versa)
        // would be half-added, and the half that is missing is only found in production.
        var all = Enum.GetValues<PlantedDateRemovalReason>();

        PlantedDateRemovalReasons.KnownReasons.Should().HaveCount(all.Length);

        foreach (var reason in all)
        {
            var wire = PlantedDateRemovalReasons.ToWire(reason);
            PlantedDateRemovalReasons.KnownReasons.Should().Contain(wire);
            PlantedDateRemovalReasons.TryParse(wire).Should().Be(reason);
            PlantedDateRemovalReasons.RenderAuditDetails("VEG000001", reason)
                .Should().StartWith("VEG000001 · ", $"{reason} must be renderable for the activity log");
        }
    }

    [Theory]
    [InlineData("Harvested")]
    [InlineData("HARVESTED")]
    [InlineData("cropfailed")]
    [InlineData("Cropfailed")]
    [InlineData("enteredbymistake")]
    [InlineData("crop_failed")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ReasonStrings_TryParse_RejectsAnythingButTheExactSpelling(string? candidate)
    {
        PlantedDateRemovalReasons.TryParse(candidate).Should().BeNull(
            "case-SENSITIVE, unlike the admin ?types= filter: this value is written by our own client into "
            + "a request body, so a different casing means the caller is guessing at the contract");
    }

    [Fact]
    public void ReasonStrings_TryParse_ToleratesSurroundingWhitespace()
    {
        PlantedDateRemovalReasons.TryParse("  cropFailed  ")
            .Should().Be(PlantedDateRemovalReason.CropFailed,
                "padding is a transport artifact; casing is a contract claim");
    }

    // The audit note.

    [Theory]
    [InlineData(PlantedDateRemovalReason.Harvested, "VEG000019 · Harvested")]
    [InlineData(PlantedDateRemovalReason.CropFailed, "VEG000019 · Crop failed")]
    [InlineData(PlantedDateRemovalReason.EnteredByMistake, "VEG000019 · Entered by mistake")]
    [InlineData(PlantedDateRemovalReason.Other, "VEG000019 · Other")]
    public void RenderAuditDetails_IsCropCodeThenReasonWord(
        PlantedDateRemovalReason reason, string expected)
    {
        PlantedDateRemovalReasons.RenderAuditDetails("VEG000019", reason).Should().Be(expected);
    }

    [Fact]
    public void RenderAuditDetails_BlankCropCode_IsTheReasonWordAlone()
    {
        // The crop code comes from the post-commit read-back, which can (rarely) fail. A note that reads
        // " · Harvested" would look like a rendering bug; the reason alone is honest and still useful.
        PlantedDateRemovalReasons.RenderAuditDetails(null, PlantedDateRemovalReason.Harvested)
            .Should().Be("Harvested");
        PlantedDateRemovalReasons.RenderAuditDetails("   ", PlantedDateRemovalReason.Other)
            .Should().Be("Other");
    }

    [Fact]
    public void RenderAuditDetails_CannotBeGivenTheFarmersNote()
    {
        // THE ENFORCEMENT IS THE SIGNATURE. Audit Details is code-authored text only; the note lives solely
        // on the PlantedDateRemovals row. A renderer that took the note could leak it from any call site,
        // so it takes a crop code and a reason and nothing else.
        var parameters = typeof(PlantedDateRemovalReasons)
            .GetMethod(nameof(PlantedDateRemovalReasons.RenderAuditDetails))!
            .GetParameters();

        parameters.Select(p => p.ParameterType).Should().Equal(
            typeof(string), typeof(PlantedDateRemovalReason));
        parameters[0].Name.Should().Be("cropCode");
    }

    // The activity-log event type.

    [Fact]
    public void ActivityEvent_WireStringAndNumberArePinned()
    {
        ((int)UserActivityEventType.PlantedDateRemoved).Should().Be(12,
            "appended after the pipeline-control events; the column stores the int, so never renumber");
        UserActivityEventStrings.ToWire(UserActivityEventType.PlantedDateRemoved)
            .Should().Be("plantedDateRemoved");
        UserActivityEventStrings.KnownTypes.Should().Contain("plantedDateRemoved",
            "the admin Logs page must be able to FILTER for it, not just render it");
        UserActivityEventStrings.TryParse("plantedDateRemoved")
            .Should().Be(UserActivityEventType.PlantedDateRemoved);
    }

    [Fact]
    public void ActivityEvent_CarriesTheFarmerAsActor_WithNoTargetOrUsername()
    {
        var row = UserActivityEvent.PlantedDateRemoved(Farmer, "VEG000001 · Harvested", Occurred);

        row.EventType.Should().Be(UserActivityEventType.PlantedDateRemoved);
        row.ActorUserId.Should().Be(Farmer, "the first event a non-admin authors");
        row.TargetUserId.Should().BeNull("a removal acts on the farmer's own data, not on another user");
        row.UsernameAttempted.Should().BeNull();
        row.Details.Should().Be("VEG000001 · Harvested");
        row.OccurredUtc.Should().Be(Occurred);
    }

    [Fact]
    public async Task ActivityEvent_WriteFailure_IsSwallowed_LikeEveryOtherAuditCall()
    {
        // No UserActivityLog table => the write throws inside UserActivityAudit and must be swallowed there.
        // The removal ROW is transactional and load-bearing; this LOG LINE is not, and a farmer's clear must
        // not fail because an admin log could not be written.
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _c = connection;

        var services = new ServiceCollection();
        services.AddDbContext<AgriForecastDbContext>(o => o.UseSqlite(connection));
        await using var provider = services.BuildServiceProvider();

        var audit = new UserActivityAudit(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<UserActivityAudit>.Instance);

        var act = () => audit.RecordPlantedDateRemovedAsync(Farmer, "VEG000001 · Harvested");

        await act.Should().NotThrowAsync();
    }

    // EF mapping. The model is built against the SQL Server provider (never connected) so the assertions are
    // about the real production mapping, not a test-only approximation.

    private static IEntityType RemovalEntityType()
    {
        var options = new DbContextOptionsBuilder<AgriForecastDbContext>()
            .UseSqlServer("Server=(model-only);Database=none;")
            .Options;
        using var ctx = new AgriForecastDbContext(options);
        return ctx.Model.FindEntityType(typeof(PlantedDateRemoval))!;
    }

    [Fact]
    public void Mapping_UsesThePluralTableName()
    {
        RemovalEntityType().GetTableName().Should().Be("PlantedDateRemovals");
    }

    [Fact]
    public void Mapping_StoresTheRemovedDateAsADateWithNoHiddenTime()
    {
        var property = RemovalEntityType().FindProperty("RemovedPlantedDate")!;

        property.IsNullable.Should().BeFalse("a removal without the date it removed records nothing");
        property.GetColumnType().Should().Be("date",
            "it was a planting DAY; a hidden 00:00:00 makes it timezone-dependent");
    }

    [Fact]
    public void Mapping_StoresTheReasonAsAnInt_AndCapsTheNote()
    {
        var et = RemovalEntityType();

        et.FindProperty("Reason")!.GetProviderClrType().Should().Be(typeof(int),
            "enum-as-int, like UserActivityEvent.EventType — the numeric values are persisted");
        et.FindProperty("Reason")!.IsNullable.Should().BeFalse(
            "a reason-less row is exactly the state this table exists to prevent");

        var note = et.FindProperty("Note")!;
        note.IsNullable.Should().BeTrue("the note is optional; the reason is not");
        note.GetMaxLength().Should().Be(PlantedDateRemoval.NoteMaxLength);
    }

    [Fact]
    public void Mapping_KeepsOccurredUtcRequired_WithNoDatabaseDefault()
    {
        var property = RemovalEntityType().FindProperty("OccurredUtc")!;

        property.IsNullable.Should().BeFalse();
        property.GetDefaultValueSql().Should().BeNull(
            "only .NET writes this table and the factory always stamps the clock, so a DB default would "
            + "only hide a caller that forgot it");
    }

    [Theory]
    // Users CASCADE, matching UserCropWatchlist: personal data does not outlive its owner.
    [InlineData("UserId", DeleteBehavior.Cascade)]
    // Crops RESTRICT: reference data cannot be deleted out from under a record that refers to it.
    [InlineData("CropId", DeleteBehavior.Restrict)]
    public void Mapping_UsesTheIntendedDeleteBehaviourPerForeignKey(string property, DeleteBehavior expected)
    {
        RemovalEntityType().GetForeignKeys()
            .Single(f => f.Properties.Single().Name == property)
            .DeleteBehavior.Should().Be(expected);
    }

    [Fact]
    public void Mapping_GivesUserAndCropNoNavigationIntoTheRemovals()
    {
        // Same leakage posture as the watchlist: if Crop ever gained a collection of removals, a careless
        // Include on a reference-data query would drag one farmer's history into another's response.
        foreach (var fk in RemovalEntityType().GetForeignKeys())
            Assert.True(
                fk.PrincipalToDependent is null,
                $"{fk.PrincipalEntityType.ClrType.Name} must gain no navigation into removal data");
    }

    [Fact]
    public void Mapping_IndexesTheFarmerCropHistoryNewestFirst()
    {
        // The DESIGN-TIME model: IsDescending is not kept in the read-optimized runtime model and throws
        // there (same reason MarketShortCodeTests reads seed data this way).
        var options = new DbContextOptionsBuilder<AgriForecastDbContext>()
            .UseSqlServer("Server=(model-only);Database=none;")
            .Options;
        using var ctx = new AgriForecastDbContext(options);

        var index = ctx.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(PlantedDateRemoval))!
            .GetIndexes()
            .Single(i => i.Name == "IX_PlantedDateRemovals_UserCropOccurredUtc");

        index.Properties.Select(p => p.Name).Should().Equal("UserId", "CropId", "OccurredUtc");
        index.IsUnique.Should().BeFalse(
            "a farmer can clear a date for the same crop more than once — every occurrence is its own row");
        index.IsDescending.Should().Equal(false, false, true);
    }

    // The migration.

    [Fact]
    public void Migration_CreatesTheTableWithTheIntendedShape()
    {
        var table = new CreatePlantedDateRemovals().UpOperations
            .OfType<CreateTableOperation>()
            .Single(c => c.Name == "PlantedDateRemovals");

        var columns = table.Columns.ToDictionary(c => c.Name);
        columns["Id"].IsNullable.Should().BeFalse();
        // Assert.NotNull rather than Should().NotBeNull(): FluentAssertions 8 binds a nullable-annotated
        // reference like Annotation? to its enum overload (CS0453).
        Assert.NotNull(columns["Id"].FindAnnotation("SqlServer:Identity"));
        columns["RemovedPlantedDate"].ColumnType.Should().Be("date");
        columns["Reason"].ColumnType.Should().Be("int");
        columns["Note"].MaxLength.Should().Be(PlantedDateRemoval.NoteMaxLength);
        columns["Note"].IsNullable.Should().BeTrue();

        // Everything that identifies WHAT was removed and WHY is NOT NULL: a row that cannot answer either
        // question would be worse than no row.
        foreach (var required in new[] { "UserId", "CropId", "RemovedPlantedDate", "Reason", "OccurredUtc" })
            columns[required].IsNullable.Should().BeFalse($"{required} is what makes the row a record");

        table.ForeignKeys.Single(f => f.PrincipalTable == "Users")
            .OnDelete.Should().Be(ReferentialAction.Cascade);
        table.ForeignKeys.Single(f => f.PrincipalTable == "Crops")
            .OnDelete.Should().Be(ReferentialAction.Restrict);
    }

    [Fact]
    public void Migration_CarriesNoBackfill()
    {
        // The table is NET-NEW, so there are no legacy rows to explain. Inventing a reason for a date cleared
        // before the reason was required would be fabricating a farmer's answer.
        new CreatePlantedDateRemovals().UpOperations
            .OfType<SqlOperation>()
            .Should().BeEmpty();
    }

    // The repository, against a real (SQLite) database — the insert must reach the table and be flushed by
    // the CALLER's SaveChanges, not by one of its own.

    private const string CreatePlantedDateRemovalsTableSql = """
        CREATE TABLE "PlantedDateRemovals" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_PlantedDateRemovals" PRIMARY KEY AUTOINCREMENT,
            "UserId" TEXT NOT NULL,
            "CropId" TEXT NOT NULL,
            "RemovedPlantedDate" TEXT NOT NULL,
            "Reason" INTEGER NOT NULL,
            "Note" TEXT NULL,
            "OccurredUtc" TEXT NOT NULL
        );
        """;

    [Fact]
    public async Task Repository_AddsWithoutSaving_SoTheCallersCommitIsTheOnlyWrite()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _c = connection;

        var options = new DbContextOptionsBuilder<AgriForecastDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var ctx = new AgriForecastDbContext(options);
        await ctx.Database.ExecuteSqlRawAsync(CreatePlantedDateRemovalsTableSql);

        var repository = new PlantedDateRemovalRepository(ctx);
        await repository.AddAsync(Record("kept whole", PlantedDateRemovalReason.EnteredByMistake));

        ctx.PlantedDateRemovals.AsNoTracking().Should().BeEmpty(
            "AddAsync only tracks: the row lands when the handler's unit of work commits, in the SAME "
            + "transaction as the cleared date");

        await ctx.SaveChangesAsync();

        var row = ctx.PlantedDateRemovals.AsNoTracking().Single();
        row.Id.Should().BeGreaterThan(0, "the store generates the identity");
        row.Reason.Should().Be(PlantedDateRemovalReason.EnteredByMistake);
        row.RemovedPlantedDate.Should().Be(Planted);
        row.Note.Should().Be("kept whole");
    }
}
