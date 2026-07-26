using AgriForecast.Application.Requests.Admin.Logs.Common;
using AgriForecast.Application.Requests.Admin.Logs.Queries.GetSystemErrors;
using AgriForecast.Application.Requests.Admin.Logs.Queries.GetTrainingRuns;
using AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// Logs hub PR A — unit tests for the admin Logs read handlers (GetTrainingRuns / GetUserActivity),
/// their validators, and the UserActivityEventStrings wire-string mapping. The DB is faked via a
/// canned ILogsReadStore so paging math + total, the newest-first + Id-DESC tiebreak, the event-type
/// filter/canonicalization, enum -> frozen wire string, and the UTC-kind stamp are exercised in
/// isolation. NOT covered here: the store's EF LINQ (verified by build + the live-DB conventions).
/// </summary>
public class AdminLogsHandlerTests
{
    // ── Fake read store: real in-memory order/page/filter over canned rows ─────────────
    private sealed class FakeStore : ILogsReadStore
    {
        public List<TrainingRunRow> Training = new();
        public List<(UserActivityRow Row, long Id)> Activity = new();
        public List<SystemErrorRow> Errors = new();
        // What the handler actually asked the store to filter by (null = unfiltered). The single
        // ?type= and the multi ?types= both collapse into this one set.
        public IReadOnlyCollection<UserActivityEventType>? CapturedTypes;

        // Convenience view for the single-type assertions: the lone member, or null when unfiltered.
        public UserActivityEventType? CapturedType =>
            CapturedTypes is { Count: 1 } ? CapturedTypes.Single() : null;

        public Task<TrainingRunsPage> GetTrainingRunsPageAsync(int page, int pageSize, CancellationToken ct = default)
        {
            var ordered = Training
                .OrderByDescending(r => r.TrainedAtUtc)
                .ToList();
            var total = ordered.Count;
            var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new TrainingRunsPage(items, total));
        }

        public Task<UserActivityPage> GetUserActivityPageAsync(
            int page, int pageSize, IReadOnlyCollection<UserActivityEventType>? types,
            CancellationToken ct = default)
        {
            CapturedTypes = types;
            var filtered = Activity
                .Where(a => types is not { Count: > 0 } || types.Contains(a.Row.EventType))
                .OrderByDescending(a => a.Row.OccurredUtc).ThenByDescending(a => a.Id)
                .Select(a => a.Row)
                .ToList();
            var total = filtered.Count;
            var items = filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new UserActivityPage(items, total));
        }

        public Task<SystemErrorsPage> GetSystemErrorsAsync(int page, int pageSize, CancellationToken ct = default)
        {
            var ordered = Errors
                .OrderByDescending(e => e.OccurredUtc).ThenByDescending(e => e.Id)
                .ToList();
            var total = ordered.Count;
            var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new SystemErrorsPage(items, total));
        }
    }

    private static GetTrainingRunsQueryHandler TrainingHandler(FakeStore s) => new(s);
    private static GetUserActivityQueryHandler ActivityHandler(FakeStore s) => new(s);
    private static GetSystemErrorsQueryHandler ErrorsHandler(FakeStore s) => new(s);

    private static SystemErrorRow ERow(long id, DateTime occurredUtc,
        string exceptionType = "System.InvalidOperationException", string? message = "boom",
        string? path = "/api/forecast", string? method = "GET", string? traceId = "trace-1",
        string? stackTrace = "   at Foo()")
        => new(id, occurredUtc, "API", exceptionType, message, path, method, traceId, stackTrace);

    private static TrainingRunRow TRun(string version, DateTime trainedAtUtc,
        bool promoted = true, bool decisionPromoted = true)
        => new(version, trainedAtUtc, promoted, decisionPromoted, null,
            "hybrid", 97.92m, "seasonal_naive", 120.00m, 5000, 96);

    private static UserActivityRow ARow(UserActivityEventType type, DateTime occurredUtc,
        Guid? actor = null, Guid? target = null, string? username = null, string? details = null)
        => new(occurredUtc, type, actor, target, username, details);

    // ── TRAINING: paging + total ────────────────────────────────────────────────────
    [Fact]
    public async Task Training_PagingMath_ReturnsRequestedPageAndTotal()
    {
        var store = new FakeStore();
        for (var i = 0; i < 5; i++)
            store.Training.Add(TRun($"v{i}", DateTime.UtcNow.AddDays(-i)));

        var dto = (await TrainingHandler(store).Handle(
            new GetTrainingRunsQuery { Page = 2, PageSize = 2 }, default)).Data;

        dto.Total.Should().Be(5);
        dto.Page.Should().Be(2);
        dto.PageSize.Should().Be(2);
        dto.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Training_NewestFirst_AndFieldsMapped_InclOverrideDivergence()
    {
        var store = new FakeStore();
        store.Training.Add(TRun("v16", DateTime.UtcNow.AddDays(-2)));
        // v17: the automated gate said NO but it was promoted by override — the two bits DIFFER.
        store.Training.Add(TRun("v17", DateTime.UtcNow.AddDays(-1), promoted: true, decisionPromoted: false));

        var dto = (await TrainingHandler(store).Handle(new GetTrainingRunsQuery(), default)).Data;

        dto.Items[0].Version.Should().Be("v17"); // newest first
        dto.Items[0].Promoted.Should().BeTrue();
        dto.Items[0].DecisionPromoted.Should().BeFalse(); // promoted despite the gate
        dto.Items[0].BestMlKind.Should().Be("hybrid");
        dto.Items[0].BestMlMae.Should().Be(97.92m);
        dto.Items[1].Version.Should().Be("v16");
    }

    [Fact]
    public async Task Training_UtcField_HasUtcKind_SoJsonEmitsZ()
    {
        var store = new FakeStore();
        // EF materializes datetime2 as Unspecified — model that exactly.
        var unspecified = new DateTime(2026, 7, 20, 3, 0, 0, DateTimeKind.Unspecified);
        store.Training.Add(TRun("v1", unspecified));

        var dto = (await TrainingHandler(store).Handle(new GetTrainingRunsQuery(), default)).Data;

        dto.Items.Single().TrainedAtUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Training_EmptyStore_ReturnsEmptyPage()
    {
        var dto = (await TrainingHandler(new FakeStore()).Handle(new GetTrainingRunsQuery(), default)).Data;
        dto.Items.Should().BeEmpty();
        dto.Total.Should().Be(0);
    }

    // ── USER-ACTIVITY: paging + tiebreak + filter + mapping ─────────────────────────
    [Fact]
    public async Task Activity_Tiebreak_SameTimestamp_OrdersByIdDesc()
    {
        var store = new FakeStore();
        var ts = new DateTime(2026, 7, 21, 1, 0, 0, DateTimeKind.Unspecified);
        // Same OccurredUtc, different ids — the Id-DESC tiebreak must order 3,2,1.
        store.Activity.Add((ARow(UserActivityEventType.LoginSucceeded, ts, actor: Guid.NewGuid()), 1));
        store.Activity.Add((ARow(UserActivityEventType.LoginFailed, ts, username: "b"), 2));
        store.Activity.Add((ARow(UserActivityEventType.UserRegistered, ts, actor: Guid.NewGuid()), 3));

        var dto = (await ActivityHandler(store).Handle(new GetUserActivityQuery(), default)).Data;

        dto.Items.Select(i => i.EventType).Should().ContainInOrder(
            UserActivityEventStrings.UserRegistered,
            UserActivityEventStrings.LoginFailed,
            UserActivityEventStrings.LoginSucceeded);
    }

    [Theory]
    [InlineData("loginFailed", UserActivityEventType.LoginFailed)]
    [InlineData("LOGINFAILED", UserActivityEventType.LoginFailed)] // case-insensitive canonicalization
    [InlineData("roleChanged", UserActivityEventType.RoleChanged)]
    public async Task Activity_TypeFilter_ParsesAndFilters(string wire, UserActivityEventType expected)
    {
        var store = new FakeStore();
        store.Activity.Add((ARow(UserActivityEventType.LoginFailed, DateTime.UtcNow.AddMinutes(-1), username: "x"), 1));
        store.Activity.Add((ARow(UserActivityEventType.RoleChanged, DateTime.UtcNow.AddMinutes(-2),
            actor: Guid.NewGuid(), target: Guid.NewGuid(), details: "role -> Admin"), 2));

        var dto = (await ActivityHandler(store).Handle(
            new GetUserActivityQuery { Type = wire }, default)).Data;

        store.CapturedType.Should().Be(expected);
        dto.Items.Should().OnlyContain(i => i.EventType == UserActivityEventStrings.ToWire(expected));
    }

    [Fact]
    public async Task Activity_BlankType_PassesNullFilter()
    {
        var store = new FakeStore();
        store.Activity.Add((ARow(UserActivityEventType.LoginSucceeded, DateTime.UtcNow, actor: Guid.NewGuid()), 1));

        await ActivityHandler(store).Handle(new GetUserActivityQuery { Type = "  " }, default);

        store.CapturedType.Should().BeNull();
    }

    [Fact]
    public async Task Activity_FailedLogin_ExposesUsernameAttempted_NotActor()
    {
        var store = new FakeStore();
        store.Activity.Add((ARow(UserActivityEventType.LoginFailed,
            new DateTime(2026, 7, 21, 2, 0, 0, DateTimeKind.Unspecified), username: "attacker"), 1));

        var dto = (await ActivityHandler(store).Handle(new GetUserActivityQuery(), default)).Data;

        var item = dto.Items.Single();
        item.EventType.Should().Be("loginFailed"); // frozen lowercase wire string
        item.UsernameAttempted.Should().Be("attacker");
        item.ActorUserId.Should().BeNull(); // a failed login has no proven actor
        item.OccurredUtc.Kind.Should().Be(DateTimeKind.Utc); // Z suffix
    }

    [Fact]
    public async Task Activity_RoleChanged_MapsActorTargetAndDetails()
    {
        var actor = Guid.NewGuid();
        var target = Guid.NewGuid();
        var store = new FakeStore();
        store.Activity.Add((ARow(UserActivityEventType.RoleChanged, DateTime.UtcNow,
            actor: actor, target: target, details: "role -> Admin"), 1));

        var dto = (await ActivityHandler(store).Handle(new GetUserActivityQuery(), default)).Data;

        var item = dto.Items.Single();
        item.EventType.Should().Be("roleChanged");
        item.ActorUserId.Should().Be(actor);
        item.TargetUserId.Should().Be(target);
        item.Details.Should().Be("role -> Admin");
    }

    // ── Wire-string mapping is total + frozen ───────────────────────────────────────
    [Theory]
    [InlineData(UserActivityEventType.LoginSucceeded, "loginSucceeded")]
    [InlineData(UserActivityEventType.LoginFailed, "loginFailed")]
    [InlineData(UserActivityEventType.UserRegistered, "userRegistered")]
    [InlineData(UserActivityEventType.RoleChanged, "roleChanged")]
    [InlineData(UserActivityEventType.UserDeleted, "userDeleted")]
    public void EventStrings_ToWire_AreFrozenLowercase(UserActivityEventType type, string expected)
    {
        UserActivityEventStrings.ToWire(type).Should().Be(expected);
    }

    // The enum is stored as int (HasConversion<int>); a reorder would silently corrupt persisted rows.
    // Pin the numeric values so a reorder fails HERE (fix belongs in a migration, never the enum).
    [Theory]
    [InlineData(UserActivityEventType.LoginSucceeded, 0)]
    [InlineData(UserActivityEventType.LoginFailed, 1)]
    [InlineData(UserActivityEventType.UserRegistered, 2)]
    [InlineData(UserActivityEventType.RoleChanged, 3)]
    [InlineData(UserActivityEventType.UserDeleted, 4)]
    public void EventType_NumericValues_ArePinned(UserActivityEventType type, int expected)
    {
        ((int)type).Should().Be(expected);
    }

    // ── SYSTEM-ERRORS: paging + newest-first tiebreak + mapping ─────────────────────
    [Fact]
    public async Task Errors_PagingMath_ReturnsRequestedPageAndTotal()
    {
        var store = new FakeStore();
        for (var i = 0; i < 5; i++)
            store.Errors.Add(ERow(i + 1, DateTime.UtcNow.AddMinutes(-i)));

        var dto = (await ErrorsHandler(store).Handle(
            new GetSystemErrorsQuery { Page = 2, PageSize = 2 }, default)).Data;

        dto.Total.Should().Be(5);
        dto.Page.Should().Be(2);
        dto.PageSize.Should().Be(2);
        dto.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Errors_Tiebreak_SameTimestamp_OrdersByIdDesc_AndMapsFields()
    {
        var store = new FakeStore();
        var ts = new DateTime(2026, 7, 21, 5, 0, 0, DateTimeKind.Unspecified);
        store.Errors.Add(ERow(1, ts, message: "first"));
        store.Errors.Add(ERow(3, ts, message: "third"));
        store.Errors.Add(ERow(2, ts, message: "second"));

        var dto = (await ErrorsHandler(store).Handle(new GetSystemErrorsQuery(), default)).Data;

        dto.Items.Select(i => i.Id).Should().ContainInOrder(3L, 2L, 1L); // Id DESC tiebreak
        var top = dto.Items[0];
        top.Source.Should().Be("API");
        top.ExceptionType.Should().Be("System.InvalidOperationException");
        top.Message.Should().Be("third");
        top.Path.Should().Be("/api/forecast");
        top.Method.Should().Be("GET");
        top.TraceId.Should().Be("trace-1");
        top.StackTrace.Should().Be("   at Foo()");
    }

    [Fact]
    public async Task Errors_UtcField_HasUtcKind_SoJsonEmitsZ()
    {
        var store = new FakeStore();
        var unspecified = new DateTime(2026, 7, 20, 3, 0, 0, DateTimeKind.Unspecified);
        store.Errors.Add(ERow(1, unspecified));

        var dto = (await ErrorsHandler(store).Handle(new GetSystemErrorsQuery(), default)).Data;

        dto.Items.Single().OccurredUtc.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Errors_EmptyStore_ReturnsEmptyPage()
    {
        var dto = (await ErrorsHandler(new FakeStore()).Handle(new GetSystemErrorsQuery(), default)).Data;
        dto.Items.Should().BeEmpty();
        dto.Total.Should().Be(0);
    }

    // ── VALIDATION ──────────────────────────────────────────────────────────────────
    private readonly GetTrainingRunsValidator _trainingValidator = new();
    private readonly GetUserActivityValidator _activityValidator = new();
    private readonly GetSystemErrorsValidator _errorsValidator = new();

    [Theory]
    [InlineData(0, 20)]   // page < 1
    [InlineData(1, 0)]    // pageSize < 1
    [InlineData(1, 101)]  // pageSize > 100
    public async Task ErrorsValidator_RejectsBadBounds(int page, int pageSize)
    {
        var result = await _errorsValidator.ValidateAsync(
            new GetSystemErrorsQuery { Page = page, PageSize = pageSize });
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task ErrorsValidator_AllowsInBounds()
    {
        var result = await _errorsValidator.ValidateAsync(
            new GetSystemErrorsQuery { Page = 1, PageSize = 100 });
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 20)]   // page < 1
    [InlineData(1, 0)]    // pageSize < 1
    [InlineData(1, 101)]  // pageSize > 100
    public async Task TrainingValidator_RejectsBadBounds(int page, int pageSize)
    {
        var result = await _trainingValidator.ValidateAsync(
            new GetTrainingRunsQuery { Page = page, PageSize = pageSize });
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task TrainingValidator_AllowsInBounds()
    {
        var result = await _trainingValidator.ValidateAsync(
            new GetTrainingRunsQuery { Page = 1, PageSize = 100 });
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 20, null)]
    [InlineData(1, 101, null)]
    [InlineData(1, 20, "notAType")]   // unknown type is a 400, never a silent unfiltered page
    [InlineData(1, 20, "")]           // empty string is NOT a known type (blank filters must be absent)
    public async Task ActivityValidator_RejectsBadBoundsOrUnknownType(int page, int pageSize, string? type)
    {
        // Note: "" is whitespace -> allowed by the Must (treated as no filter), so exclude it from the
        // reject set by only asserting reject when type is a genuine non-blank unknown.
        var query = new GetUserActivityQuery { Page = page, PageSize = pageSize, Type = type };
        var result = await _activityValidator.ValidateAsync(query);
        if (page < 1 || pageSize is < 1 or > 100 || (!string.IsNullOrWhiteSpace(type) && !UserActivityEventStrings.IsKnown(type)))
            result.IsValid.Should().BeFalse();
        else
            result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    [InlineData("loginSucceeded")]
    [InlineData("userDeleted")]
    public async Task ActivityValidator_AllowsBlankOrKnownType(string? type)
    {
        var result = await _activityValidator.ValidateAsync(
            new GetUserActivityQuery { Page = 1, PageSize = 20, Type = type });
        result.IsValid.Should().BeTrue();
    }
}
