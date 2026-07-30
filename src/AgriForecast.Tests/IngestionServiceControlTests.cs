using System.Security.Claims;
using AgriForecast.API.Controllers;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.Ingestion.Commands.StartIngestionService;
using AgriForecast.Application.Requests.Admin.Ingestion.Commands.StopIngestionService;
using AgriForecast.Application.Requests.Admin.Ingestion.Common;
using AgriForecast.Application.Requests.Admin.Logs.Common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Enums;
using AgriForecast.Infrastructure.Services.IngestionControl;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// The admin ingestion play/stop control, exercised from the CONTROLLER down through the real handlers.
/// <para>
/// What these pin, and why each matters:
/// (1) the WIRE CONTRACT the admin UI is built against — 202 {batchId} / 202 {} and the three 409 codes —
///     because the FE switches on those exact strings;
/// (2) start is guarded TWICE, by the run-row pre-check and by the cross-process application lock, and
///     either guard alone refuses with the same code, so a second pass cannot be launched over the first;
/// (3) the pass runs on a token the REQUEST does not own, or it would be cancelled the instant the 202 was
///     written, and the lock lease plus the hosted-pass registration are both released when it ends;
/// (4) stop tells the truth: it can cancel a pass this process started, and it says "not_stoppable" rather
///     than 202 for a pass running on the scheduled worker, which it has no channel to reach.
/// </para>
/// The DB, the SQL application lock and the pass itself are faked; SqlIngestionPassLock's own T-SQL is not
/// covered here.
/// </summary>
public class IngestionServiceControlTests
{
    // Test doubles.

    // Read store with only the two reads the control handlers use; the rest throw, so a handler that
    // starts consulting something else fails loudly rather than silently passing.
    private sealed class ControlReadStore : IIngestionReadStore
    {
        public DateTime? LatestUnfinishedStartedUtc;
        public IReadOnlyCollection<string>? CapturedExcluded;

        public Task<DateTime?> GetLatestUnfinishedStartedUtcAsync(
            IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
        {
            CapturedExcluded = excludeSources;
            return Task.FromResult(LatestUnfinishedStartedUtc);
        }

        public Task<int> GetRunCountAsync(IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IngestionRunHeadRow?> GetLatestRunAsync(IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<IngestionRunStatus>> GetRunStatusesForBatchAsync(
            Guid batchId, IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IngestionVerificationRow?> GetLatestVerificationAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<IngestionWatermarkRow>> GetWatermarksAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IngestionRunsPage> GetRunsPageAsync(int page, int pageSize, string? source, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IngestionVerificationRow>> GetLatestVerificationsByBatchAsync(
            IReadOnlyCollection<Guid> batchIds, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FixedSettings : IIngestionStatusSettings
    {
        public string ServiceAddress => "test-host";
        public int RunningStalenessMinutes { get; init; } = 120;
    }

    // In-memory stand-in for sp_getapplock: one boolean of shared state, so contention is deterministic.
    private sealed class FakePassLock : IIngestionPassLock
    {
        private bool _held;
        public int AcquireAttempts { get; private set; }
        public int Releases { get; private set; }

        // Pretend another host (the CronJob worker) already holds it.
        public void HoldExternally() => _held = true;

        public bool IsHeld => _held;

        public Task<IIngestionPassLease?> TryAcquireAsync(CancellationToken ct = default)
        {
            AcquireAttempts++;
            if (_held) return Task.FromResult<IIngestionPassLease?>(null);
            _held = true;
            return Task.FromResult<IIngestionPassLease?>(new Lease(this));
        }

        private sealed class Lease : IIngestionPassLease
        {
            private readonly FakePassLock _owner;
            public Lease(FakePassLock owner) => _owner = owner;

            public ValueTask DisposeAsync()
            {
                _owner._held = false;
                _owner.Releases++;
                return ValueTask.CompletedTask;
            }
        }
    }

    // Captures the background work instead of racing the thread pool, so a test can decide exactly when
    // the pass runs (or never let it finish, to model an in-flight pass).
    private sealed class CapturingLauncher : IBackgroundWorkLauncher
    {
        public Func<Task>? Captured;
        public void Run(Func<Task> work) => Captured = work;
        public Task RunCapturedAsync() => Captured is null ? Task.CompletedTask : Captured();
    }

    // A pass that records the batchId and token it was given, and can be held open until released.
    private sealed class RecordingPassRunner : IIngestionPassRunner
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid? ObservedBatchId;
        public CancellationToken ObservedToken;
        public bool Started;
        public bool BlockUntilReleased;

        public void Release() => _gate.TrySetResult();

        public async Task RunPassAsync(Guid batchId, CancellationToken ct)
        {
            Started = true;
            ObservedBatchId = batchId;
            ObservedToken = ct;
            if (BlockUntilReleased)
                await _gate.Task;
        }
    }

    // Records the pipeline-control audit calls (the other members are never used by these handlers).
    private sealed class RecordingAudit : IUserActivityAudit
    {
        public readonly List<(UserActivityEventType Type, Guid Actor, Guid BatchId)> Written = new();

        public Task RecordIngestionServiceStartedAsync(Guid actingAdminId, Guid batchId, CancellationToken ct = default)
        {
            Written.Add((UserActivityEventType.IngestionServiceStarted, actingAdminId, batchId));
            return Task.CompletedTask;
        }

        public Task RecordIngestionServiceStopRequestedAsync(Guid actingAdminId, Guid batchId, CancellationToken ct = default)
        {
            Written.Add((UserActivityEventType.IngestionServiceStopRequested, actingAdminId, batchId));
            return Task.CompletedTask;
        }

        public Task RecordLoginSucceededAsync(Guid actorUserId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordLoginFailedAsync(string? usernameAttempted, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordUserRegisteredAsync(Guid actorUserId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordUserCreatedByAdminAsync(Guid actingAdminId, Guid newUserId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordRoleChangedAsync(Guid actorUserId, Guid targetUserId, string newRole, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordUserDeletedAsync(Guid actorUserId, Guid targetUserId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordPolicyFlagChangedAsync(Guid a, ContentChangeAction b, string? c, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordFestivalChangedAsync(Guid a, ContentChangeAction b, string? c, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordNewsEventChangedAsync(Guid a, ContentChangeAction b, string? c, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordCropChangedAsync(Guid a, ContentChangeAction b, string? c, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordMarketChangedAsync(Guid a, ContentChangeAction b, string? c, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordPlantedDateRemovedAsync(Guid a, string? b, CancellationToken ct = default) => Task.CompletedTask;
    }

    // A controller context carrying (or deliberately lacking) the JWT subject claim the controller reads
    // through ActingUserExtensions.GetActingUserId().
    private static DefaultHttpContext HttpContextFor(Guid userId)
    {
        var ctx = new DefaultHttpContext();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "TestAuth"));
        return ctx;
    }

    private static DefaultHttpContext HttpContextWithoutSubject() => new();

    // Harness: the real controller over the real handlers, with the seams above substituted.
    private sealed class Harness
    {
        public required AdminIngestionController Controller { get; init; }
        public required ControlReadStore Store { get; init; }
        public required FakePassLock Lock { get; init; }
        public required CapturingLauncher Launcher { get; init; }
        public required RecordingPassRunner Runner { get; init; }
        public required RecordingAudit Audit { get; init; }
        public required IApiHostedIngestionPasses HostedPasses { get; init; }
        public required Guid Admin { get; init; }
    }

    private static Harness Build(int stalenessMinutes = 120)
    {
        var store = new ControlReadStore();
        var settings = new FixedSettings { RunningStalenessMinutes = stalenessMinutes };
        var passLock = new FakePassLock();
        var hosted = new ApiHostedIngestionPasses();
        var runner = new RecordingPassRunner();
        var launcher = new CapturingLauncher();
        var audit = new RecordingAudit();

        var start = new StartIngestionServiceCommandHandler(
            store, settings, passLock, hosted, runner, launcher, audit,
            NullLogger<StartIngestionServiceCommandHandler>.Instance);
        var stop = new StopIngestionServiceCommandHandler(
            hosted, store, settings, audit,
            NullLogger<StopIngestionServiceCommandHandler>.Instance);

        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<StartIngestionServiceCommand>(), It.IsAny<CancellationToken>()))
            .Returns((StartIngestionServiceCommand c, CancellationToken ct) => start.Handle(c, ct));
        mediator
            .Setup(m => m.Send(It.IsAny<StopIngestionServiceCommand>(), It.IsAny<CancellationToken>()))
            .Returns((StopIngestionServiceCommand c, CancellationToken ct) => stop.Handle(c, ct));

        var admin = Guid.NewGuid();
        var controller = new AdminIngestionController(mediator.Object);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = HttpContextFor(admin)
        };

        return new Harness
        {
            Controller = controller,
            Store = store,
            Lock = passLock,
            Launcher = launcher,
            Runner = runner,
            Audit = audit,
            HostedPasses = hosted,
            Admin = admin
        };
    }

    // Assertion helpers. Assert.IsType rather than FluentAssertions: this project's Should() overload set
    // does not resolve on IActionResult/object.

    private static Guid AcceptedBatchId(IActionResult result)
    {
        var accepted = Assert.IsType<AcceptedResult>(result);
        accepted.StatusCode.Should().Be(202);
        var dto = Assert.IsType<IngestionServiceStart_Dto>(accepted.Value);
        return dto.BatchId;
    }

    private static void ShouldBeEmptyAccepted(IActionResult result)
    {
        var accepted = Assert.IsType<AcceptedResult>(result);
        accepted.StatusCode.Should().Be(202);
        Assert.NotNull(accepted.Value);
        // {} on the wire: an object with no properties, never a null body the FE's JSON parse would choke on.
        accepted.Value!.GetType().GetProperties().Should().BeEmpty();
    }

    // The 409 body is a FLAT { "error": "<code>" } — deliberately NOT the { errors: [{ property, message }] }
    // envelope the validation paths use. The admin UI reads body.error directly, so the shape is pinned
    // here property-by-property: an extra or renamed field would break the button silently.
    private static void ShouldBeConflict(IActionResult result, string expectedCode)
    {
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        conflict.StatusCode.Should().Be(409);
        Assert.NotNull(conflict.Value);

        var properties = conflict.Value!.GetType().GetProperties();
        properties.Should().ContainSingle("the conflict body carries exactly one field")
            .Which.Name.Should().Be("error");

        var error = properties[0].GetValue(conflict.Value) as string;
        error.Should().Be(expectedCode);

        // Serialize to be certain the wire form is what the FE parses, not just the CLR shape.
        System.Text.Json.JsonSerializer.Serialize(conflict.Value)
            .Should().Be($$"""{"error":"{{expectedCode}}"}""");
    }

    // START.

    [Fact]
    public async Task Start_WhenIdle_Accepts202WithBatchId_AndRunsThePassInTheBackground()
    {
        var h = Build();

        var result = await h.Controller.StartService();

        var batchId = AcceptedBatchId(result);
        batchId.Should().NotBeEmpty();

        // 202 is answered BEFORE the pass runs — the request must not block on a multi-minute pass.
        h.Runner.Started.Should().BeFalse("the pass is launched in the background, not awaited by the request");
        h.Launcher.Captured.Should().NotBeNull();

        await h.Launcher.RunCapturedAsync();

        h.Runner.Started.Should().BeTrue();
        h.Runner.ObservedBatchId.Should().Be(batchId,
            "the batchId handed to the admin must be the one every run row of the pass carries");
    }

    [Fact]
    public async Task Start_WhenAFreshUnfinishedRunRowExists_Conflicts_AlreadyRunning()
    {
        var h = Build();
        h.Store.LatestUnfinishedStartedUtc = DateTime.UtcNow.AddMinutes(-5);

        var result = await h.Controller.StartService();

        ShouldBeConflict(result, IngestionServiceControlErrors.AlreadyRunning);
        h.Lock.AcquireAttempts.Should().Be(0,
            "the cheap run-row pre-check should short-circuit before opening a lock connection");
        h.Launcher.Captured.Should().BeNull("no pass may be launched");
    }

    [Fact]
    public async Task Start_WhenTheApplicationLockIsHeldElsewhere_Conflicts_AlreadyRunning()
    {
        var h = Build();
        // No run row at all — the other host's audit write has not landed (or never will). The lock is the
        // only thing that knows, which is exactly why a Running row is not the mutex.
        h.Store.LatestUnfinishedStartedUtc = null;
        h.Lock.HoldExternally();

        var result = await h.Controller.StartService();

        ShouldBeConflict(result, IngestionServiceControlErrors.AlreadyRunning);
        h.Lock.AcquireAttempts.Should().Be(1);
        h.Launcher.Captured.Should().BeNull();
    }

    [Fact]
    public async Task Start_WhenTheUnfinishedRunRowIsStale_Starts()
    {
        var h = Build(stalenessMinutes: 120);
        // A pass that crashed hours ago left a null-FinishedUtc breadcrumb. It must not lock the admin out
        // of ever starting ingestion again.
        h.Store.LatestUnfinishedStartedUtc = DateTime.UtcNow.AddMinutes(-121);

        var result = await h.Controller.StartService();

        AcceptedBatchId(result).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Start_RunRowCheck_ExcludesTheSourcesExcludedFromServiceState()
    {
        var h = Build();

        await h.Controller.StartService();

        // Same policy as the status card: a hung Python FEATURE_BUILD row must not read as "ingestion is
        // running" and block the start button.
        h.Store.CapturedExcluded.Should().BeEquivalentTo(IngestionSources.ExcludedFromServiceState);
        h.Store.CapturedExcluded.Should().Contain(IngestionSources.FeatureBuild);
        // PR 0c reviewer B2: a Running FORECAST_SNAPSHOT row must likewise never be misread as "ingestion
        // is running" and block Start/Stop -- IsRunningPerRunRowsAsync is shared by both handlers, so this
        // one assertion covers Stop too.
        h.Store.CapturedExcluded.Should().Contain(IngestionSources.ForecastSnapshot);
    }

    [Fact]
    public async Task Start_RunsThePassOnATokenTheRequestDoesNotOwn()
    {
        var h = Build();
        using var requestAborted = new CancellationTokenSource();
        h.Controller.HttpContext.RequestAborted = requestAborted.Token;

        await h.Controller.StartService();

        // The response has completed; ASP.NET trips RequestAborted.
        requestAborted.Cancel();
        await h.Launcher.RunCapturedAsync();

        h.Runner.ObservedToken.IsCancellationRequested.Should().BeFalse(
            "a pass bound to the request token would die the moment the 202 was written");
    }

    [Fact]
    public async Task Start_ReleasesTheLockAndDeregisters_WhenThePassEnds()
    {
        var h = Build();

        await h.Controller.StartService();
        h.Lock.IsHeld.Should().BeTrue("the lease is held for the whole pass, not just the request");
        h.HostedPasses.IsRunning.Should().BeTrue();

        await h.Launcher.RunCapturedAsync();

        h.Lock.IsHeld.Should().BeFalse();
        h.Lock.Releases.Should().Be(1);
        h.HostedPasses.IsRunning.Should().BeFalse("a finished pass must not be reported as stoppable");
    }

    [Fact]
    public async Task Start_ReleasesTheLock_EvenWhenThePassThrows()
    {
        var h = Build();
        var throwingRunner = new Mock<IIngestionPassRunner>();
        throwingRunner
            .Setup(r => r.RunPassAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("catastrophic pass failure"));

        var handler = new StartIngestionServiceCommandHandler(
            h.Store, new FixedSettings(), h.Lock, h.HostedPasses, throwingRunner.Object, h.Launcher, h.Audit,
            NullLogger<StartIngestionServiceCommandHandler>.Instance);

        await handler.Handle(new StartIngestionServiceCommand { ActingUserId = h.Admin }, default);

        // The launcher's real implementation swallows and logs; here we just prove the finally ran.
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Launcher.RunCapturedAsync());

        h.Lock.IsHeld.Should().BeFalse("an undisposed lease would wedge every later pass");
        h.HostedPasses.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Start_AuditsIngestionServiceStarted_WithTheActingAdminAndBatchId()
    {
        var h = Build();

        var batchId = AcceptedBatchId(await h.Controller.StartService());

        h.Audit.Written.Should().ContainSingle();
        var row = h.Audit.Written.Single();
        row.Type.Should().Be(UserActivityEventType.IngestionServiceStarted);
        row.Actor.Should().Be(h.Admin, "the actor is the JWT subject, never a request-body value");
        row.BatchId.Should().Be(batchId);
    }

    [Fact]
    public async Task Start_WritesNoAuditRow_WhenItRefuses()
    {
        var h = Build();
        h.Lock.HoldExternally();

        await h.Controller.StartService();

        h.Audit.Written.Should().BeEmpty("a refused start is not a control action that happened");
    }

    [Fact]
    public async Task Start_WithoutAJwtSubject_Is401AndNeverTouchesTheLock()
    {
        var h = Build();
        h.Controller.ControllerContext.HttpContext = HttpContextWithoutSubject();

        var result = await h.Controller.StartService();

        Assert.IsType<UnauthorizedObjectResult>(result);
        h.Lock.AcquireAttempts.Should().Be(0);
    }

    // STOP.

    [Fact]
    public async Task Stop_WhenNothingIsRunning_Conflicts_NotRunning()
    {
        var h = Build();
        h.Store.LatestUnfinishedStartedUtc = null;

        ShouldBeConflict(await h.Controller.StopService(), IngestionServiceControlErrors.NotRunning);
        h.Audit.Written.Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_WhenThePassIsRunningOnAnotherHost_Conflicts_NotStoppable()
    {
        var h = Build();
        // A fresh unfinished run row with nothing registered here: the 21:00 CronJob worker is mid-pass.
        h.Store.LatestUnfinishedStartedUtc = DateTime.UtcNow.AddMinutes(-3);

        ShouldBeConflict(await h.Controller.StopService(), IngestionServiceControlErrors.NotStoppable);
        h.Audit.Written.Should().BeEmpty(
            "nothing was stopped, so recording a stop request would be a false audit entry");
    }

    [Fact]
    public async Task Stop_CancelsTheApiHostedPass_AndAccepts202()
    {
        var h = Build();
        h.Runner.BlockUntilReleased = true;

        var batchId = AcceptedBatchId(await h.Controller.StartService());
        var passTask = h.Launcher.RunCapturedAsync();   // the pass is now in flight
        h.Runner.Started.Should().BeTrue();

        var stopResult = await h.Controller.StopService();

        ShouldBeEmptyAccepted(stopResult);
        h.Runner.ObservedToken.IsCancellationRequested.Should().BeTrue(
            "stop signals the pass's own cancellation token");

        // Stop returns immediately; the pass unwinds on its own schedule.
        h.Runner.Release();
        await passTask;
        h.HostedPasses.IsRunning.Should().BeFalse();
        h.Lock.IsHeld.Should().BeFalse();

        var row = h.Audit.Written.Should().ContainSingle(w =>
            w.Type == UserActivityEventType.IngestionServiceStopRequested).Subject;
        row.Actor.Should().Be(h.Admin);
        row.BatchId.Should().Be(batchId, "the audit row must name the pass that was asked to stop");
    }

    [Fact]
    public async Task Stop_AfterTheHostedPassFinished_Conflicts_NotRunning()
    {
        var h = Build();

        await h.Controller.StartService();
        await h.Launcher.RunCapturedAsync();   // pass completes and de-registers
        h.Store.LatestUnfinishedStartedUtc = null;

        ShouldBeConflict(await h.Controller.StopService(), IngestionServiceControlErrors.NotRunning);
    }

    [Fact]
    public async Task Stop_WithoutAJwtSubject_Is401()
    {
        var h = Build();
        h.Controller.ControllerContext.HttpContext = HttpContextWithoutSubject();

        Assert.IsType<UnauthorizedObjectResult>(await h.Controller.StopService());
    }

    // The hosted-pass registry on its own.

    [Fact]
    public void HostedPasses_ReportNothingStoppable_WhenIdleOrAfterDisposal()
    {
        var passes = new ApiHostedIngestionPasses();
        passes.IsRunning.Should().BeFalse();
        passes.TryRequestStop(out var none).Should().BeFalse();
        none.Should().BeEmpty();

        var batchId = Guid.NewGuid();
        var handle = passes.Begin(batchId);
        passes.IsRunning.Should().BeTrue();

        handle.Dispose();
        passes.IsRunning.Should().BeFalse();
        passes.TryRequestStop(out _).Should().BeFalse();
    }

    [Fact]
    public void HostedPasses_CancelTheHandleToken_OnStop()
    {
        var passes = new ApiHostedIngestionPasses();
        var batchId = Guid.NewGuid();
        using var handle = passes.Begin(batchId);

        passes.TryRequestStop(out var reported).Should().BeTrue();

        reported.Should().Be(batchId);
        handle.Token.IsCancellationRequested.Should().BeTrue();
    }

    // Wire-contract pins.

    [Fact]
    public void StartAcceptedBody_SerialisesAsBatchIdOnly()
    {
        var batchId = Guid.NewGuid();

        // ASP.NET Core's default (web) JSON options camelCase the property, and Guid.ToString() is
        // lowercase — the casing the ML side and every batchId query already use.
        var json = System.Text.Json.JsonSerializer.Serialize(
            new IngestionServiceStart_Dto { BatchId = batchId },
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        json.Should().Be($$"""{"batchId":"{{batchId}}"}""");
    }

    [Fact]
    public void ControlErrorCodes_AreFrozenSnakeCase()
    {
        IngestionServiceControlErrors.AlreadyRunning.Should().Be("already_running");
        IngestionServiceControlErrors.NotRunning.Should().Be("not_running");
        IngestionServiceControlErrors.NotStoppable.Should().Be("not_stoppable");
        IngestionServiceControlErrors.All.Should().HaveCount(3);
        IngestionServiceControlErrors.IsConflict("already_running").Should().BeTrue();
        IngestionServiceControlErrors.IsConflict("Failed to generate market code.").Should().BeFalse(
            "an ordinary handler error must fall through to 400, not masquerade as a state conflict");
    }

    // Appended after the content events (5..9), never renumbered — the column stores the int.
    [Theory]
    [InlineData(UserActivityEventType.IngestionServiceStarted, 10)]
    [InlineData(UserActivityEventType.IngestionServiceStopRequested, 11)]
    public void PipelineEventType_NumericValues_ArePinned(UserActivityEventType type, int expected)
    {
        ((int)type).Should().Be(expected);
    }

    [Theory]
    [InlineData(UserActivityEventType.IngestionServiceStarted, "ingestionServiceStarted")]
    [InlineData(UserActivityEventType.IngestionServiceStopRequested, "ingestionServiceStopRequested")]
    public void PipelineEventStrings_ToWire_AreFrozenCamelCase(UserActivityEventType type, string expected)
    {
        UserActivityEventStrings.ToWire(type).Should().Be(expected);
        // Renderable is not enough: the Logs page must also be able to FILTER on it.
        UserActivityEventStrings.KnownTypes.Should().Contain(expected);
        UserActivityEventStrings.TryParse(expected).Should().Be(type);
    }

    [Fact]
    public void PassLock_ResourceName_IsSharedAcrossHosts()
    {
        // Every host that can run a pass must ask for the SAME application-lock resource, or the lock
        // stops being mutual. Pinned so a rename cannot silently disable single-flight.
        SqlIngestionPassLock.ResourceName.Should().Be("AgriForecast:IngestionPass");
    }

    [Fact]
    public void PassConfiguration_RequiresTheStructuralKeys_ButNotTheSecret()
    {
        IngestionPassConfiguration.RequiredKeys.Should().BeEquivalentTo(new[]
        {
            "MarketPriceSources:DambullaDec:BaseUrl",
            "MlService:BaseUrl"
        });

        // Deliberate: the admin API key is a secret, empty in appsettings by design. Failing the whole API
        // boot over it would take every farmer-facing endpoint down for an admin-only feature.
        IngestionPassConfiguration.RequiredKeys.Should().NotContain("MlService:AdminApiKey");
    }

    [Fact]
    public void PassConfiguration_NamesEveryMissingKeyAtOnce()
    {
        var empty = new ConfigurationBuilder().Build();

        var act = () => IngestionPassConfiguration.ThrowIfIncomplete(empty);

        act.Should().Throw<InvalidOperationException>()
            .Where(ex => ex.Message.Contains("MarketPriceSources:DambullaDec:BaseUrl")
                         && ex.Message.Contains("MlService:BaseUrl"),
                "one restart should reveal the whole gap, not one key per boot attempt");
    }

    [Fact]
    public void PassConfiguration_PassesWhenEveryStructuralKeyIsSet()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MarketPriceSources:DambullaDec:BaseUrl"] = "https://api.dambulladec.com/",
                ["MlService:BaseUrl"] = "http://127.0.0.1:8077"
            })
            .Build();

        IngestionPassConfiguration.FindMissingKeys(config).Should().BeEmpty();
        IngestionPassConfiguration.ThrowIfIncomplete(config);
    }
}
