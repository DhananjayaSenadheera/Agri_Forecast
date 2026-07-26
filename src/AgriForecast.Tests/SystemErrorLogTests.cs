using AgriForecast.Domain.Entities;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// Behavioural tests for the SystemError entity and the SystemErrorLog writer. Load-bearing guarantees:
/// the factory hard-caps every over-long field and turns blanks into null so an over-long value can never
/// reach SQL; a write failure is swallowed and logged and never throws into the request; and the
/// process-wide storm guard admits at most 60 writes per rolling minute, admitting again in the next window.
/// </summary>
public class SystemErrorLogTests
{
    // Deterministic clock so the storm-guard window is testable without wall-clock sleeps.
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;
        public TestClock(DateTimeOffset start) => _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }

    // Column affinities match EF's SQLite mapping (DateTime->TEXT, string->TEXT, long->INTEGER). Id is
    // INTEGER PK AUTOINCREMENT so EF's store-generated bigint identity round-trips.
    private const string CreateSystemErrorsSql = """
        CREATE TABLE "SystemErrors" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_SystemErrors" PRIMARY KEY AUTOINCREMENT,
            "OccurredUtc" TEXT NOT NULL,
            "Source" TEXT NOT NULL,
            "ExceptionType" TEXT NOT NULL,
            "Message" TEXT NULL,
            "StackTrace" TEXT NULL,
            "Path" TEXT NULL,
            "Method" TEXT NULL,
            "TraceId" TEXT NULL
        );
        """;

    private static async Task<(SqliteConnection conn, ServiceProvider provider, IServiceScopeFactory scopes)>
        BuildSqliteAsync(bool createTable)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AgriForecastDbContext>(o => o.UseSqlite(connection));
        var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        if (createTable)
        {
            using var createScope = scopeFactory.CreateScope();
            await createScope.ServiceProvider.GetRequiredService<AgriForecastDbContext>()
                .Database.ExecuteSqlRawAsync(CreateSystemErrorsSql);
        }

        return (connection, provider, scopeFactory);
    }

    private static async Task<int> CountRowsAsync(IServiceScopeFactory scopes)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AgriForecastDbContext>()
            .SystemErrors.CountAsync();
    }

    // Entity caps.
    [Fact]
    public void FromException_HardCaps_OverlongMessagePathAndStack()
    {
        var ex = new InvalidOperationException(new string('m', 2000));
        // A stack trace can only be set by throwing; simulate an oversized one another way: the factory
        // pulls exception.StackTrace, so throw-and-catch a wrapper carrying a long path/method instead.
        var row = SystemError.FromException(
            ex, "API", new string('M', 50), new string('p', 500), new string('t', 200),
            new DateTime(2026, 7, 21, 0, 0, 0, DateTimeKind.Utc));

        row.Message!.Length.Should().Be(1000);
        row.Path!.Length.Should().Be(200);
        row.Method!.Length.Should().Be(10);
        row.TraceId!.Length.Should().Be(50);
        row.Source.Should().Be("API");
        row.ExceptionType.Should().Be("System.InvalidOperationException");
    }

    [Fact]
    public void FromException_StackTrace_HardCapped_To8000()
    {
        // Force an oversized stack via a subclass that overrides StackTrace.
        var oversized = new OversizedStackException(new string('s', 9000));

        var row = SystemError.FromException(
            oversized, "API", "GET", "/x", "t", DateTime.UtcNow);

        row.StackTrace!.Length.Should().Be(8000);
    }

    private sealed class OversizedStackException : Exception
    {
        private readonly string _stack;
        public OversizedStackException(string stack) : base("oversized") => _stack = stack;
        public override string? StackTrace => _stack;
    }

    [Fact]
    public void FromException_BlankFields_StoreNull()
    {
        var ex = new Exception(""); // blank message
        var row = SystemError.FromException(ex, "API", "   ", "  ", null, DateTime.UtcNow);

        row.Message.Should().BeNull();
        row.Path.Should().BeNull();
        row.Method.Should().BeNull();
        row.TraceId.Should().BeNull();
    }

    // Writer fail-safe.
    [Fact]
    public async Task RecordAsync_WhenSaveFails_IsSwallowed_AndNeverThrows()
    {
        // No table => the SaveChanges will fail. The writer must swallow-and-log, never throw.
        var (connection, provider, scopeFactory) = await BuildSqliteAsync(createTable: false);
        await using var _c = connection;
        await using var _p = provider;

        var writer = new SystemErrorLog(scopeFactory, NullLogger<SystemErrorLog>.Instance,
            new TestClock(DateTimeOffset.UnixEpoch));

        var act = () => writer.RecordAsync(
            new InvalidOperationException("boom"), "GET", "/api/forecast", "trace-1", CancellationToken.None);

        await act.Should().NotThrowAsync("error logging must never break the response it records");
    }

    [Fact]
    public async Task RecordAsync_PersistsRow_WithTypeMethodAndPathOnly()
    {
        var (connection, provider, scopeFactory) = await BuildSqliteAsync(createTable: true);
        await using var _c = connection;
        await using var _p = provider;

        var writer = new SystemErrorLog(scopeFactory, NullLogger<SystemErrorLog>.Instance,
            new TestClock(DateTimeOffset.UnixEpoch));

        await writer.RecordAsync(
            new InvalidOperationException("boom"), "POST", "/api/forecast/predict", "trace-9", CancellationToken.None);

        using var scope = scopeFactory.CreateScope();
        var row = scope.ServiceProvider.GetRequiredService<AgriForecastDbContext>().SystemErrors.Single();
        row.Source.Should().Be("API");
        row.ExceptionType.Should().Be("System.InvalidOperationException");
        row.Message.Should().Be("boom");
        row.Method.Should().Be("POST");
        row.Path.Should().Be("/api/forecast/predict");
        row.TraceId.Should().Be("trace-9");
    }

    // Storm guard.
    [Fact]
    public async Task StormGuard_Admits60PerWindow_Drops61st_AndAdmitsAgainNextWindow()
    {
        var (connection, provider, scopeFactory) = await BuildSqliteAsync(createTable: true);
        await using var _c = connection;
        await using var _p = provider;

        var clock = new TestClock(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero));
        var writer = new SystemErrorLog(scopeFactory, NullLogger<SystemErrorLog>.Instance, clock);

        // 61 writes in the SAME minute: the first 60 are admitted, the 61st is dropped.
        for (var i = 0; i < 61; i++)
            await writer.RecordAsync(new InvalidOperationException("boom"), "GET", "/x", "t", CancellationToken.None);

        (await CountRowsAsync(scopeFactory)).Should().Be(60, "the 61st write in a window is dropped");

        // Roll into the next window: the guard admits again.
        clock.Advance(TimeSpan.FromMinutes(1));
        await writer.RecordAsync(new InvalidOperationException("boom"), "GET", "/x", "t", CancellationToken.None);

        (await CountRowsAsync(scopeFactory)).Should().Be(61, "the next minute window admits fresh writes");
    }
}
