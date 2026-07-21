using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// Behavioural tests for UserActivityAudit — the fire-safe audit writer used at the five auth/user
/// call sites. Load-bearing guarantees: (1) an audit write runs in its OWN scope so it can NEVER
/// flush the caller's pending ChangeTracker state (B1 isolation, mirrors IngestionRunRepository);
/// (2) an audit-write FAILURE is swallowed-and-logged and can never throw into the auth op; (3) each
/// intent method writes exactly the fields its event type allows (no password/token ever stored).
/// </summary>
public class UserActivityAuditTests
{
    // Column affinities match EF's SQLite mapping (GUID/DateTime->TEXT, enum/long->INTEGER, string->
    // TEXT). Id is INTEGER PK AUTOINCREMENT so EF's store-generated bigint identity round-trips.
    private const string CreateUserActivityLogSql = """
        CREATE TABLE "UserActivityLog" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_UserActivityLog" PRIMARY KEY AUTOINCREMENT,
            "OccurredUtc" TEXT NOT NULL,
            "EventType" INTEGER NOT NULL,
            "ActorUserId" TEXT NULL,
            "TargetUserId" TEXT NULL,
            "UsernameAttempted" TEXT NULL,
            "Details" TEXT NULL
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

        // Create ONLY the UserActivityLog table (the sole table the audit touches). EnsureCreated is
        // avoided because the full model's IngestionVerifications ISJSON check + the ModelTrainingRuns
        // SYSUTCDATETIME() default are not SQLite functions.
        if (createTable)
        {
            using var createScope = scopeFactory.CreateScope();
            await createScope.ServiceProvider.GetRequiredService<AgriForecastDbContext>()
                .Database.ExecuteSqlRawAsync(CreateUserActivityLogSql);
        }

        return (connection, provider, scopeFactory);
    }

    [Fact]
    public async Task AuditWrite_IsIsolatedFrom_TheCallersScopedContext()
    {
        var (connection, provider, scopeFactory) = await BuildSqliteAsync(createTable: true);
        await using var _c = connection;
        await using var _p = provider;

        var audit = new UserActivityAudit(scopeFactory, NullLogger<UserActivityAudit>.Instance);
        var actor = Guid.NewGuid();

        // Simulate the login/registration/admin handler's scoped context holding an UNSAVED
        // (bogus/poisoned) tracked entity. If the audit shared this context, its SaveChanges would
        // flush this too.
        using (var callerScope = scopeFactory.CreateScope())
        {
            var callerCtx = callerScope.ServiceProvider.GetRequiredService<AgriForecastDbContext>();
            var bogus = UserActivityEvent.LoginFailed("BOGUS_UNSAVED", DateTime.UtcNow);
            callerCtx.UserActivityLog.Add(bogus); // tracked, NEVER saved

            // Real audit write via its OWN isolated scope.
            await audit.RecordLoginSucceededAsync(actor, CancellationToken.None);
        } // caller context disposed WITHOUT ever saving the bogus entity

        using var verifyScope = scopeFactory.CreateScope();
        var verifyCtx = verifyScope.ServiceProvider.GetRequiredService<AgriForecastDbContext>();
        var persisted = verifyCtx.UserActivityLog.ToList();

        persisted.Should().ContainSingle(e => e.EventType == UserActivityEventType.LoginSucceeded)
            .Which.ActorUserId.Should().Be(actor,
                "the audited event is persisted through the audit's own context");
        persisted.Should().NotContain(e => e.UsernameAttempted == "BOGUS_UNSAVED",
            "the audit write uses its OWN context — the caller's unsaved entity must never be flushed by it");
    }

    [Fact]
    public async Task AuditWrite_Failure_IsSwallowed_AndNeverThrowsIntoTheCaller()
    {
        // No table created => the SaveChanges will fail. The audit must swallow-and-log, never throw.
        var (connection, provider, scopeFactory) = await BuildSqliteAsync(createTable: false);
        await using var _c = connection;
        await using var _p = provider;

        var audit = new UserActivityAudit(scopeFactory, NullLogger<UserActivityAudit>.Instance);

        var act = () => audit.RecordUserRegisteredAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().NotThrowAsync("an audit-write failure must never break the audited operation");
    }

    [Fact]
    public async Task RoleChanged_RendersDetails_AndCarriesActorAndTarget()
    {
        var (connection, provider, scopeFactory) = await BuildSqliteAsync(createTable: true);
        await using var _c = connection;
        await using var _p = provider;

        var audit = new UserActivityAudit(scopeFactory, NullLogger<UserActivityAudit>.Instance);
        var actor = Guid.NewGuid();
        var target = Guid.NewGuid();

        await audit.RecordRoleChangedAsync(actor, target, "Admin", CancellationToken.None);

        using var verifyScope = scopeFactory.CreateScope();
        var row = verifyScope.ServiceProvider.GetRequiredService<AgriForecastDbContext>()
            .UserActivityLog.Single();

        row.EventType.Should().Be(UserActivityEventType.RoleChanged);
        row.ActorUserId.Should().Be(actor);
        row.TargetUserId.Should().Be(target);
        row.Details.Should().Be("role -> Admin");
        row.UsernameAttempted.Should().BeNull("a role change never carries an attempted-username string");
    }

    [Fact]
    public async Task LoginFailed_StoresAttemptedUsername_NoActor()
    {
        var (connection, provider, scopeFactory) = await BuildSqliteAsync(createTable: true);
        await using var _c = connection;
        await using var _p = provider;

        var audit = new UserActivityAudit(scopeFactory, NullLogger<UserActivityAudit>.Instance);

        await audit.RecordLoginFailedAsync("  someuser  ", CancellationToken.None); // padded -> trimmed

        using var verifyScope = scopeFactory.CreateScope();
        var row = verifyScope.ServiceProvider.GetRequiredService<AgriForecastDbContext>()
            .UserActivityLog.Single();

        row.EventType.Should().Be(UserActivityEventType.LoginFailed);
        row.UsernameAttempted.Should().Be("someuser");
        row.ActorUserId.Should().BeNull("a failed login has no proven actor");
    }

    // The privacy caps are the one guard between an oversized input and a column-overflow
    // audit failure — prove they truncate rather than trusting the factory comment.
    [Fact]
    public void LoginFailed_username_longer_than_column_is_hard_capped_to_100()
    {
        var oversized = new string('u', 150);

        var row = UserActivityEvent.LoginFailed(oversized, DateTime.UtcNow);

        row.UsernameAttempted.Should().HaveLength(100);
        row.UsernameAttempted.Should().Be(new string('u', 100));
    }

    [Fact]
    public void RoleChanged_details_longer_than_column_is_hard_capped_to_500()
    {
        var oversized = "role -> " + new string('x', 600);

        var row = UserActivityEvent.RoleChanged(Guid.NewGuid(), Guid.NewGuid(), oversized, DateTime.UtcNow);

        row.Details.Should().HaveLength(500);
        row.Details.Should().StartWith("role -> ");
    }

    [Fact]
    public void LoginFailed_blank_username_stores_null_not_empty_string()
    {
        UserActivityEvent.LoginFailed("   ", DateTime.UtcNow).UsernameAttempted.Should().BeNull();
        UserActivityEvent.LoginFailed(null, DateTime.UtcNow).UsernameAttempted.Should().BeNull();
    }
}
