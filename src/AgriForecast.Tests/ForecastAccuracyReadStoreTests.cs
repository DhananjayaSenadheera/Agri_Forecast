using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.Services.ForecastAccuracyRead;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Tests;

/// <summary>
/// ForecastAccuracyReadStore against a real (SQLite) database, for the same reason UserSaleTests runs
/// its read store against one: an in-memory fake answers LINQ no database ever sees, so the one class
/// of bug the handler suite cannot catch is EF failing to TRANSLATE the store's own query.
/// GetGroupCensusAsync is the store's first composite-key GroupBy projecting two aggregates into a
/// record constructor — if that stopped translating, every /summary call would 500 while every
/// handler-level test stayed green. GetMaturedScoringRowsAsync joined the coverage with A4: it now
/// inner-joins Crops inside an eleven-column record projection, the same translation risk class.
/// <para>
/// HONEST LIMIT: SQLite proves translation, grouping shape, the aggregates and the window filter — NOT
/// SQL Server's case-INSENSITIVE collation. SQLite compares TEXT case-sensitively, so the known,
/// accepted collation divergence between the SQL GROUP BY and the aggregation layer's ordinal grouping
/// (see the note in ForecastAccuracyReadStore.GetSnapshotsPageAsync and the FakeStore comment in
/// ForecastAccuracyHandlerTests) is not reproducible here and is not claimed.
/// </para>
/// </summary>
public class ForecastAccuracyReadStoreTests
{
    private const string Model = "residual";
    private const string Fallback = "crop_mean_fallback";

    // Only the columns the two store queries under test touch — EF selects exactly these, so a full
    // model would add maintenance without adding coverage (same minimal-table approach as
    // UserSaleTests; EnsureCreated is avoided because the full model's BIN2 CHECK constraints and
    // SYSUTCDATETIME() defaults are not SQLite). The scoring columns joined the table for
    // GetMaturedScoringRowsAsync's projection test; Crops exists (Id + Name only) because that read
    // inner-joins it for the display name.
    private const string CreateForecastSnapshotsTableSql = """
        CREATE TABLE "ForecastSnapshots" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_ForecastSnapshots" PRIMARY KEY,
            "CropId" TEXT NOT NULL,
            "SnapshotDate" TEXT NOT NULL,
            "HarvestDate" TEXT NULL,
            "GrowthPeriodDays" INTEGER NULL,
            "PredictedPrice" TEXT NOT NULL,
            "ActualPrice" TEXT NULL,
            "ReferencePrice" TEXT NULL,
            "SignedError" TEXT NULL,
            "PercentageError" TEXT NULL,
            "WithinInterval" INTEGER NULL,
            "ActivePredictor" TEXT NOT NULL,
            "ModelVersion" TEXT NULL,
            "MaturityState" TEXT NOT NULL
        );
        CREATE TABLE "Crops" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_Crops" PRIMARY KEY,
            "Name" TEXT NOT NULL
        );
        """;

    private static async Task<AgriForecastDbContext> SnapshotsDbAsync(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<AgriForecastDbContext>()
            .UseSqlite(connection)
            .Options;

        var ctx = new AgriForecastDbContext(options);
        await ctx.Database.ExecuteSqlRawAsync(CreateForecastSnapshotsTableSql);
        return ctx;
    }

    // The census tests never read crop or scoring columns, so one shared filler crop id and price keep
    // their call sites unchanged; the scoring-projection test passes everything explicitly.
    private static readonly Guid FillerCropId = Guid.NewGuid();

    // PARAMETERISED, not string-interpolated into the SQL text, for the reason UserSaleTests gives for
    // its ids: the values must be written through the SAME provider type mapping EF reads and filters
    // them with — a hand-formatted date literal spelled differently from EF's DateOnly mapping would
    // silently fall out of the window and look exactly like a broken filter.
    private static Task SeedAsync(
        AgriForecastDbContext ctx, DateOnly snapshotDate, string predictor, string? modelVersion,
        string state, DateOnly? harvestDate,
        Guid? cropId = null,
        int? growthPeriodDays = null,
        decimal predictedPrice = 100m,
        decimal? actualPrice = null,
        decimal? referencePrice = null,
        decimal? signedError = null,
        decimal? percentageError = null,
        bool? withinInterval = null)
        => ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO "ForecastSnapshots"
                 ("Id", "CropId", "SnapshotDate", "HarvestDate", "GrowthPeriodDays", "PredictedPrice",
                  "ActualPrice", "ReferencePrice", "SignedError", "PercentageError", "WithinInterval",
                  "ActivePredictor", "ModelVersion", "MaturityState")
             VALUES ({Guid.NewGuid()}, {cropId ?? FillerCropId}, {snapshotDate}, {harvestDate},
                     {growthPeriodDays}, {predictedPrice}, {actualPrice}, {referencePrice},
                     {signedError}, {percentageError}, {withinInterval}, {predictor}, {modelVersion},
                     {state});
             """);

    private static Task SeedCropAsync(AgriForecastDbContext ctx, Guid id, string name)
        => ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "Crops" ("Id", "Name") VALUES ({id}, {name});""");

    private static ForecastSnapshotGroupCensusRow Cell(
        IReadOnlyList<ForecastSnapshotGroupCensusRow> cells, string predictor, string? version, string state)
        => cells.Single(c =>
            c.ActivePredictor == predictor && c.ModelVersion == version && c.MaturityState == state);

    [Fact]
    public async Task GetGroupCensus_TranslatesAndAggregates_AgainstARealDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _c = connection;

        await using var ctx = await SnapshotsDbAsync(connection);

        var from = new DateOnly(2026, 6, 1);

        // residual/v16 — three pending inside the window (one exactly ON the >= boundary), one matured,
        // one not_maturable with the null harvest date that state implies.
        await SeedAsync(ctx, new DateOnly(2026, 6, 10), Model, "v16",
            ForecastSnapshotMaturityStates.Pending, new DateOnly(2026, 9, 20));
        await SeedAsync(ctx, new DateOnly(2026, 6, 11), Model, "v16",
            ForecastSnapshotMaturityStates.Pending, new DateOnly(2026, 9, 15)); // the cell's min
        await SeedAsync(ctx, from, Model, "v16",
            ForecastSnapshotMaturityStates.Pending, new DateOnly(2026, 11, 2)); // boundary row — >= keeps it
        await SeedAsync(ctx, new DateOnly(2026, 6, 12), Model, "v16",
            ForecastSnapshotMaturityStates.Matured, new DateOnly(2026, 8, 1));
        await SeedAsync(ctx, new DateOnly(2026, 6, 17), Model, "v16",
            ForecastSnapshotMaturityStates.NotMaturable, null);

        // residual/v17 — its own pending cell with its own min.
        await SeedAsync(ctx, new DateOnly(2026, 6, 13), Model, "v17",
            ForecastSnapshotMaturityStates.Pending, new DateOnly(2026, 10, 5));

        // crop_mean_fallback with NO recorded version — two pending rows and one terminal, all of which
        // must land in NULL-version buckets, not one bucket per row.
        await SeedAsync(ctx, new DateOnly(2026, 6, 14), Fallback, null,
            ForecastSnapshotMaturityStates.Pending, new DateOnly(2026, 9, 1));
        await SeedAsync(ctx, new DateOnly(2026, 6, 15), Fallback, null,
            ForecastSnapshotMaturityStates.Pending, new DateOnly(2026, 9, 3));
        await SeedAsync(ctx, new DateOnly(2026, 6, 16), Fallback, null,
            ForecastSnapshotMaturityStates.ActualUnavailable, new DateOnly(2026, 5, 1));

        // Aged out: one day before the cutoff, carrying the earliest harvest date of all — it must
        // neither count in residual/v16's pending cell nor drag that cell's min backwards.
        await SeedAsync(ctx, from.AddDays(-1), Model, "v16",
            ForecastSnapshotMaturityStates.Pending, new DateOnly(2026, 2, 1));

        var store = new ForecastAccuracyReadStore(ctx);

        // The load-bearing call: a client-eval failure (EF unable to translate the grouped record
        // projection) would throw right here — the fact a list comes back IS the translation proof.
        var cells = await store.GetGroupCensusAsync(from);

        // Grouping shape: one cell per (predictor, version, state) with rows in the window — never one
        // per row, and never a fused cell across versions or states.
        cells.Should().HaveCount(6);

        var v16Pending = Cell(cells, Model, "v16", ForecastSnapshotMaturityStates.Pending);
        v16Pending.Count.Should().Be(3, "the >= boundary row is inside; the aged-out row is not");
        v16Pending.EarliestHarvestDate.Should().Be(new DateOnly(2026, 9, 15),
            "MIN over the windowed pending rows only — the aged-out row's 2026-02-01 must not win");

        // Note: .Should() is not called on the record itself — FluentAssertions 8 binds a
        // nullable-annotated reference to its enum overload (CS0453), same as UserSaleTests notes.
        var v16Matured = Cell(cells, Model, "v16", ForecastSnapshotMaturityStates.Matured);
        v16Matured.Count.Should().Be(1);
        v16Matured.EarliestHarvestDate.Should().Be(new DateOnly(2026, 8, 1));

        var notMaturable = Cell(cells, Model, "v16", ForecastSnapshotMaturityStates.NotMaturable);
        notMaturable.Count.Should().Be(1);
        notMaturable.EarliestHarvestDate.Should().BeNull("MIN over a null harvest date is null, not a throw");

        var v17Pending = Cell(cells, Model, "v17", ForecastSnapshotMaturityStates.Pending);
        v17Pending.Count.Should().Be(1);
        v17Pending.EarliestHarvestDate.Should().Be(new DateOnly(2026, 10, 5));

        // The NULL-version rows fold into ONE bucket per state — SQL GROUP BY treats NULL keys as one
        // group, which is exactly what the null-version summary group depends on.
        var fallbackPending = Cell(cells, Fallback, null, ForecastSnapshotMaturityStates.Pending);
        fallbackPending.Count.Should().Be(2);
        fallbackPending.EarliestHarvestDate.Should().Be(new DateOnly(2026, 9, 1));

        var fallbackUnavailable = Cell(cells, Fallback, null, ForecastSnapshotMaturityStates.ActualUnavailable);
        fallbackUnavailable.Count.Should().Be(1);
        fallbackUnavailable.EarliestHarvestDate.Should().Be(new DateOnly(2026, 5, 1));
    }

    // The A4 projection: matured rows now carry CropId, the JOINED crop name and GrowthPeriodDays for
    // the horizon buckets, macro-averages and worst-crops ranking. The call itself is the translation
    // proof (an inner join feeding an eleven-column record constructor); the assertions pin that the
    // join resolves the RIGHT crop per row, that the added columns arrive as stored, and that the
    // matured-only and window filters were not disturbed by extending the projection.
    [Fact]
    public async Task GetMaturedScoringRows_ProjectsCropAndGrowthPeriod_AgainstARealDatabase()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _c = connection;

        await using var ctx = await SnapshotsDbAsync(connection);

        var carrot = Guid.NewGuid();
        var beans = Guid.NewGuid();
        await SeedCropAsync(ctx, carrot, "Carrot");
        await SeedCropAsync(ctx, beans, "Beans");
        await SeedCropAsync(ctx, FillerCropId, "Filler");

        var from = new DateOnly(2026, 6, 1);

        // Two matured rows inside the window, one per crop, with distinct scoring columns.
        await SeedAsync(ctx, new DateOnly(2026, 6, 10), Model, "v16",
            ForecastSnapshotMaturityStates.Matured, new DateOnly(2026, 9, 8),
            cropId: carrot, growthPeriodDays: 90, predictedPrice: 120.50m, actualPrice: 118.00m,
            referencePrice: 115.25m, signedError: 2.50m, percentageError: 2.1186m, withinInterval: true);
        await SeedAsync(ctx, new DateOnly(2026, 6, 11), Fallback, null,
            ForecastSnapshotMaturityStates.Matured, new DateOnly(2026, 7, 26),
            cropId: beans, growthPeriodDays: 45, predictedPrice: 80.00m, actualPrice: 100.00m,
            referencePrice: 80.00m, signedError: -20.00m, percentageError: -20.0000m, withinInterval: false);
        // A matured row whose optional columns are ALL null (growth period included) must come back as
        // nulls, not vanish and not throw — the unknown-bucket input.
        await SeedAsync(ctx, new DateOnly(2026, 6, 12), Fallback, null,
            ForecastSnapshotMaturityStates.Matured, new DateOnly(2026, 8, 1),
            cropId: beans, growthPeriodDays: null);
        // Excluded: a pending row inside the window, and a matured row one day before the cutoff.
        await SeedAsync(ctx, new DateOnly(2026, 6, 13), Model, "v16",
            ForecastSnapshotMaturityStates.Pending, new DateOnly(2026, 9, 11),
            cropId: carrot, growthPeriodDays: 90);
        await SeedAsync(ctx, from.AddDays(-1), Model, "v16",
            ForecastSnapshotMaturityStates.Matured, new DateOnly(2026, 8, 29),
            cropId: carrot, growthPeriodDays: 90);

        var rows = await new ForecastAccuracyReadStore(ctx).GetMaturedScoringRowsAsync(from);

        rows.Should().HaveCount(3, "matured-only and the >= window filter must survive the wider projection");

        var carrotRow = rows.Single(r => r.CropId == carrot);
        carrotRow.CropName.Should().Be("Carrot"); // the JOINED name, per row, not a lookup artifact
        carrotRow.GrowthPeriodDays.Should().Be(90);
        carrotRow.ActivePredictor.Should().Be(Model);
        carrotRow.ModelVersion.Should().Be("v16");
        carrotRow.PredictedPrice.Should().Be(120.50m);
        carrotRow.ActualPrice.Should().Be(118.00m);
        carrotRow.ReferencePrice.Should().Be(115.25m);
        carrotRow.SignedError.Should().Be(2.50m);
        carrotRow.PercentageError.Should().Be(2.1186m);
        carrotRow.WithinInterval.Should().BeTrue();

        var beansScored = rows.Single(r => r.CropId == beans && r.GrowthPeriodDays == 45);
        beansScored.CropName.Should().Be("Beans");
        beansScored.PercentageError.Should().Be(-20.0000m);
        beansScored.WithinInterval.Should().BeFalse();

        var beansNulls = rows.Single(r => r.CropId == beans && r.GrowthPeriodDays == null);
        beansNulls.CropName.Should().Be("Beans");
        beansNulls.ActualPrice.Should().BeNull();
        beansNulls.PercentageError.Should().BeNull();
        beansNulls.WithinInterval.Should().BeNull();
    }

    [Fact]
    public async Task GetGroupCensus_EmptyTable_IsAnEmptyList_NotAnError()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var _c = connection;

        await using var ctx = await SnapshotsDbAsync(connection);

        var cells = await new ForecastAccuracyReadStore(ctx)
            .GetGroupCensusAsync(new DateOnly(2026, 6, 1));

        cells.Should().BeEmpty("a summary over an empty ledger is a normal answer, not a failure");
    }
}
