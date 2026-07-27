using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using AgriForecast.API.Startup.Sentinel;
using AgriForecast.Application.Requests.Admin.Pipeline;
using AgriForecast.Application.Requests.Admin.Pipeline.Queries.GetPipelineHealth;
using AgriForecast.Application.Services;
using AgriForecast.Infrastructure.Services.PipelineHealth;
using AgriForecast.Infrastructure.Services.PipelineSentinel;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// The nightly pipeline email sentinel: the thing that tells the owner a night went wrong without them
/// opening the app.
/// <para>
/// The decision matrix is pinned state by state — green sends a heartbeat (or nothing when the heartbeat
/// is off), every non-green state sends an alert, and nothing at all is sent when the sentinel cannot
/// see or cannot mail. NO test may put a message on the wire: every one of them runs against a fake
/// mailer, and the only real mailer touched is constructed to read its IsConfigured flag, never to send.
/// </para>
/// <para>
/// Time is virtual. The clock ADVANCES BY THE REQUESTED DELAY whenever the code under test waits, so the
/// 30-minute re-check loop and the give-up deadline are exercised at their real durations in
/// milliseconds of wall time, with exact assertions on how long was waited.
/// </para>
/// </summary>
public class PipelineSentinelTests
{
    // 22:30 Asia/Colombo (UTC+05:30, no DST) on 2026-07-26 is 17:00Z — the default check time, 90 minutes
    // after the 21:00 fire (15:30Z) the night is named for.
    private static readonly DateTime CheckUtc = new(2026, 7, 26, 17, 0, 0, DateTimeKind.Utc);
    private const string Night = "2026-07-26";

    // ---- doubles -----------------------------------------------------------------------------------

    // A clock that jumps forward by exactly the delay asked of it. CreateTimer advances the clock
    // synchronously and then fires the callback off the thread pool, which is what makes
    // Task.Delay(delay, timeProvider, ct) return "later" in virtual time but immediately in real time.
    private sealed class VirtualTimeProvider : TimeProvider
    {
        private long _utcTicks;

        public VirtualTimeProvider(DateTime utcNow) => _utcTicks = utcNow.Ticks;

        public List<TimeSpan> Waits { get; } = new();

        public override DateTimeOffset GetUtcNow() =>
            new(new DateTime(Interlocked.Read(ref _utcTicks), DateTimeKind.Utc), TimeSpan.Zero);

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime > TimeSpan.Zero)
            {
                lock (Waits) Waits.Add(dueTime);
                Interlocked.Add(ref _utcTicks, dueTime.Ticks);
            }

            return new ImmediateTimer(callback, state);
        }

        private sealed class ImmediateTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private int _fired;

            public ImmediateTimer(TimerCallback callback, object? state)
            {
                _callback = callback;
                _state = state;
                ThreadPool.UnsafeQueueUserWorkItem(_ => Fire(), null);
            }

            private void Fire()
            {
                if (Interlocked.Exchange(ref _fired, 1) == 0) _callback(_state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeMailer : ISentinelMailer
    {
        public bool IsConfigured { get; init; } = true;
        public bool ThrowOnSend { get; init; }
        public Action? OnSend { get; init; }
        public List<SentinelEmail> Sent { get; } = new();

        public Task SendAsync(SentinelEmail email, CancellationToken cancellationToken = default)
        {
            OnSend?.Invoke();
            if (ThrowOnSend) throw new InvalidOperationException("smtp is down");
            Sent.Add(email);
            return Task.CompletedTask;
        }
    }

    // Returns the scripted snapshots in order and then repeats the last one forever, so "a night that
    // never finishes" is expressible without an infinite literal.
    private sealed class FakeProbe : IPipelineHealthProbe
    {
        private readonly Queue<PipelineHealth_GetDto?> _scripted;
        private PipelineHealth_GetDto? _last;

        public FakeProbe(params PipelineHealth_GetDto?[] snapshots)
        {
            _scripted = new Queue<PipelineHealth_GetDto?>(snapshots);
            _last = snapshots.Length == 0 ? null : snapshots[^1];
        }

        public bool Throws { get; init; }
        public int Calls { get; private set; }

        /// <summary>When set, every read records the virtual instant it happened at.</summary>
        public TimeProvider? Clock { get; init; }

        public List<DateTime> Instants { get; } = new();

        /// <summary>Stops an otherwise endless hosted-service loop after N reads.</summary>
        public CancellationTokenSource? StopAfter { get; init; }

        public int StopAfterCalls { get; init; }

        public Task<PipelineHealth_GetDto?> ReadAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Clock is not null) Instants.Add(Clock.GetUtcNow().UtcDateTime);
            if (Throws) throw new InvalidOperationException("the database is unreachable");
            if (_scripted.Count > 0) _last = _scripted.Dequeue();
            if (StopAfter is not null && Calls >= StopAfterCalls) StopAfter.Cancel();
            return Task.FromResult(_last);
        }
    }

    // An SMTP endpoint that accepts the TCP connection and then says NOTHING, ever — no 220 greeting, no
    // close. This is the failure SmtpClient.Timeout does not cover, and the one that would otherwise
    // park the nightly loop forever.
    private sealed class BlackholeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly List<TcpClient> _accepted = new();
        private readonly CancellationTokenSource _cts = new();

        public BlackholeSmtpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                        lock (_accepted) _accepted.Add(client); // hold it open and stay silent
                    }
                }
                catch
                {
                    // Listener stopped or token cancelled — expected on teardown.
                }
            });
        }

        public int Port { get; }

        public void Dispose()
        {
            _cts.Cancel();
            lock (_accepted)
            {
                foreach (var client in _accepted) client.Dispose();
            }

            _listener.Stop();
            _cts.Dispose();
        }
    }

    private sealed class TestSentinelSettings : ISentinelSettings
    {
        public TimeOnly LocalCheckTime { get; init; } = new(22, 30);
        public bool SendGreenHeartbeat { get; init; } = true;
        public int RunningRecheckMinutes { get; init; } = 30;
        public string AdminLogsUrl { get; init; } = "http://localhost:30080/admin/logs/ingestion";
    }

    // ---- helpers -----------------------------------------------------------------------------------

    private static PipelineHealth_GetDto Snapshot(string state) => new()
    {
        ExpectedForDate = Night,
        State = state,
        BatchId = state == PipelineHealthStates.Missing ? null : Guid.Parse("6f9619ff-8b86-d011-b42d-00cf4fc964ff"),
        StartedUtc = state == PipelineHealthStates.Missing ? null : new DateTime(2026, 7, 26, 15, 32, 10, DateTimeKind.Utc),
        VerificationStatus = state == PipelineHealthStates.GateBlocked ? "Fail" : "Pass",
        FeatureBuildStatus = state == PipelineHealthStates.Green ? "succeeded" : null,
        CheckedAtUtc = CheckUtc
    };

    // The REAL schedule settings, so Asia/Colombo resolution and its fallbacks are under test here too.
    private static readonly IPipelineScheduleSettings Schedule =
        new PipelineScheduleSettings(new ConfigurationBuilder().Build());

    private static PipelineSentinel Build(
        IPipelineHealthProbe probe,
        ISentinelMailer mailer,
        VirtualTimeProvider clock,
        ISentinelSettings? settings = null) =>
        new(probe, mailer, settings ?? new TestSentinelSettings(), Schedule, clock,
            NullLogger<PipelineSentinel>.Instance);

    // ---- the decision matrix ------------------------------------------------------------------------

    // "missing" is deliberately absent here: it is the one non-green state that is NOT terminal on
    // sight, because the catch-up window may still be open. It has its own three tests below.
    [Theory]
    [InlineData(PipelineHealthStates.Failed, "stopped with an error")]
    // "quality check", not "data verification" — the phrase the UI uses (en.json qualityCheck).
    [InlineData(PipelineHealthStates.GateBlocked, "blocked at the quality check")]
    [InlineData(PipelineHealthStates.Partial, "ran partially")]
    public async Task Every_non_green_state_sends_one_alert_naming_the_state_and_the_night(
        string state, string expectedCopy)
    {
        var mailer = new FakeMailer();
        var sentinel = Build(new FakeProbe(Snapshot(state)), mailer, new VirtualTimeProvider(CheckUtc));

        var outcome = await sentinel.RunNightlyCheckAsync(CancellationToken.None);

        outcome.Should().Be(SentinelOutcome.AlertSent);
        mailer.Sent.Should().ContainSingle();

        var email = mailer.Sent[0];
        // The subject is what the owner sees on a phone lock screen, so it must carry both facts.
        email.Subject.Should().Be($"AgriForecast pipeline: {state} for {Night}");
        email.Body.Should().Contain(state).And.Contain(Night);
        // Same words as the admin banner — the mail and the screen tell ONE story.
        email.Body.Should().Contain(expectedCopy);
    }

    [Fact]
    public async Task Green_sends_the_all_clear_heartbeat_when_it_is_enabled()
    {
        var mailer = new FakeMailer();
        var sentinel = Build(
            new FakeProbe(Snapshot(PipelineHealthStates.Green)), mailer, new VirtualTimeProvider(CheckUtc));

        var outcome = await sentinel.RunNightlyCheckAsync(CancellationToken.None);

        outcome.Should().Be(SentinelOutcome.HeartbeatSent);
        mailer.Sent.Should().ContainSingle();
        mailer.Sent[0].Subject.Should().Be($"AgriForecast pipeline: green for {Night}");
        // The heartbeat has to say WHY it exists, or its absence means nothing to the reader.
        mailer.Sent[0].Body.Should().Contain("silence");
    }

    [Fact]
    public async Task Green_sends_nothing_when_the_heartbeat_is_switched_off()
    {
        var mailer = new FakeMailer();
        var sentinel = Build(
            new FakeProbe(Snapshot(PipelineHealthStates.Green)), mailer, new VirtualTimeProvider(CheckUtc),
            new TestSentinelSettings { SendGreenHeartbeat = false });

        var outcome = await sentinel.RunNightlyCheckAsync(CancellationToken.None);

        outcome.Should().Be(SentinelOutcome.HeartbeatSuppressed);
        mailer.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task The_heartbeat_switch_never_silences_an_alert()
    {
        var mailer = new FakeMailer();
        var sentinel = Build(
            new FakeProbe(Snapshot(PipelineHealthStates.Failed)), mailer, new VirtualTimeProvider(CheckUtc),
            new TestSentinelSettings { SendGreenHeartbeat = false });

        var outcome = await sentinel.RunNightlyCheckAsync(CancellationToken.None);

        outcome.Should().Be(SentinelOutcome.AlertSent);
        mailer.Sent.Should().ContainSingle();
    }

    // ---- the still-running night --------------------------------------------------------------------

    [Fact]
    public async Task A_running_night_is_re_checked_until_it_reaches_a_terminal_state()
    {
        var clock = new VirtualTimeProvider(CheckUtc);
        var probe = new FakeProbe(
            Snapshot(PipelineHealthStates.Running),
            Snapshot(PipelineHealthStates.Running),
            Snapshot(PipelineHealthStates.Failed));
        var mailer = new FakeMailer();

        var outcome = await Build(probe, mailer, clock).RunNightlyCheckAsync(CancellationToken.None);

        // Two waits of exactly the configured interval, three reads, and the email describes the FINAL
        // state — never the transient "running" the first read saw.
        clock.Waits.Should().Equal(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        probe.Calls.Should().Be(3);
        outcome.Should().Be(SentinelOutcome.AlertSent);
        mailer.Sent.Should().ContainSingle();
        mailer.Sent[0].Subject.Should().Be($"AgriForecast pipeline: failed for {Night}");
    }

    [Fact]
    public async Task A_night_that_never_finishes_is_reported_as_running_before_the_next_fire()
    {
        var clock = new VirtualTimeProvider(CheckUtc);
        var mailer = new FakeMailer();
        var probe = new FakeProbe(Snapshot(PipelineHealthStates.Running));

        var outcome = await Build(probe, mailer, clock).RunNightlyCheckAsync(CancellationToken.None);

        // Give-up instant = the next 21:00 Colombo fire (15:30Z on the 27th) minus one 30-minute
        // interval = 15:00Z. From 17:00Z that is 22 hours, i.e. 44 waits — and then it STOPS, rather
        // than waiting through the next night's run and reporting on a muddled window.
        clock.Waits.Should().HaveCount(44);
        clock.Waits.Should().OnlyContain(w => w == TimeSpan.FromMinutes(30));
        clock.GetUtcNow().UtcDateTime.Should().Be(new DateTime(2026, 7, 27, 15, 0, 0, DateTimeKind.Utc));

        // Still not a terminal state — so it is reported honestly as unfinished, not quietly dropped and
        // not dressed up as a failure it was never observed to be.
        outcome.Should().Be(SentinelOutcome.AlertSent);
        mailer.Sent[0].Subject.Should().Be($"AgriForecast pipeline: running for {Night}");
        mailer.Sent[0].Body.Should().Contain("never reached a finished state");
    }

    // ---- "missing" and the catch-up window ----------------------------------------------------------
    //
    // The 21:00 CronJob carries startingDeadlineSeconds 21600 (CatchUpWindowMinutes 360), so a node
    // asleep at 21:00 that wakes at 02:00 still runs the pipeline and still counts as this night. The
    // 22:30 check sits INSIDE that window. Emailing "did not run at all" at 22:30 and having the banner
    // read green the next morning would spend the alert's credibility on the exact state it exists to
    // catch — so "missing" is provisional until the window closes at 03:00 Colombo (21:30Z).

    [Fact]
    public async Task A_night_that_starts_late_inside_the_catch_up_window_is_never_alerted_as_missing()
    {
        var clock = new VirtualTimeProvider(CheckUtc);
        var probe = new FakeProbe(
            Snapshot(PipelineHealthStates.Missing),   // 22:30 — nothing has started YET
            Snapshot(PipelineHealthStates.Missing),   // 23:00 — still nothing
            Snapshot(PipelineHealthStates.Partial));  // 23:30 — the late run landed
        var mailer = new FakeMailer();

        var outcome = await Build(probe, mailer, clock).RunNightlyCheckAsync(CancellationToken.None);

        clock.Waits.Should().Equal(TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
        outcome.Should().Be(SentinelOutcome.AlertSent);
        mailer.Sent.Should().ContainSingle();
        // The real verdict, and NOT the premature "did not run" that would have been a false alarm.
        mailer.Sent[0].Subject.Should().Be($"AgriForecast pipeline: partial for {Night}");
        mailer.Sent[0].Body.Should().NotContain("did not run");
    }

    [Fact]
    public async Task A_missing_night_is_alerted_once_the_catch_up_window_closes_empty()
    {
        var clock = new VirtualTimeProvider(CheckUtc);
        var mailer = new FakeMailer();
        var probe = new FakeProbe(Snapshot(PipelineHealthStates.Missing));

        var outcome = await Build(probe, mailer, clock).RunNightlyCheckAsync(CancellationToken.None);

        // 21:00 Colombo fire = 15:30Z; + 360 minutes = 21:30Z, i.e. 03:00 Colombo. From the 17:00Z check
        // that is 4.5 hours, so nine 30-minute re-reads and then the verdict — ONE alert, not nine.
        clock.Waits.Should().HaveCount(9);
        clock.GetUtcNow().UtcDateTime.Should().Be(new DateTime(2026, 7, 26, 21, 30, 0, DateTimeKind.Utc));
        outcome.Should().Be(SentinelOutcome.AlertSent);
        mailer.Sent.Should().ContainSingle();
        mailer.Sent[0].Subject.Should().Be($"AgriForecast pipeline: missing for {Night}");
        mailer.Sent[0].Body.Should().Contain("did not run");
    }

    [Fact]
    public async Task A_missing_night_is_alerted_immediately_when_the_window_has_already_closed()
    {
        // 22:00Z is 03:30 Colombo the next morning: the fire being reported on is still 21:00 on the
        // 26th, and its catch-up window shut half an hour ago. Nothing left to wait for.
        var clock = new VirtualTimeProvider(new DateTime(2026, 7, 26, 22, 0, 0, DateTimeKind.Utc));
        var mailer = new FakeMailer();

        var outcome = await Build(new FakeProbe(Snapshot(PipelineHealthStates.Missing)), mailer, clock)
            .RunNightlyCheckAsync(CancellationToken.None);

        clock.Waits.Should().BeEmpty();
        outcome.Should().Be(SentinelOutcome.AlertSent);
        mailer.Sent[0].Subject.Should().Be($"AgriForecast pipeline: missing for {Night}");
    }

    // ---- fail-open ----------------------------------------------------------------------------------

    [Fact]
    public async Task An_unconfigured_mailer_disables_the_sentinel_without_reading_anything()
    {
        var probe = new FakeProbe(Snapshot(PipelineHealthStates.Failed));
        var mailer = new FakeMailer { IsConfigured = false };

        var outcome = await Build(probe, mailer, new VirtualTimeProvider(CheckUtc))
            .RunNightlyCheckAsync(CancellationToken.None);

        outcome.Should().Be(SentinelOutcome.Disabled);
        probe.Calls.Should().Be(0);
        mailer.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_mailer_that_throws_is_swallowed_and_never_escapes_into_the_host()
    {
        var mailer = new FakeMailer { ThrowOnSend = true };
        var sentinel = Build(
            new FakeProbe(Snapshot(PipelineHealthStates.Failed)), mailer, new VirtualTimeProvider(CheckUtc));

        var outcome = await sentinel.RunNightlyCheckAsync(CancellationToken.None);

        outcome.Should().Be(SentinelOutcome.SendFailed);
        mailer.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_probe_that_throws_sends_nothing_rather_than_guessing_a_verdict()
    {
        var mailer = new FakeMailer();
        var probe = new FakeProbe(Snapshot(PipelineHealthStates.Green)) { Throws = true };

        var outcome = await Build(probe, mailer, new VirtualTimeProvider(CheckUtc))
            .RunNightlyCheckAsync(CancellationToken.None);

        // The DB being unreachable means the API does not KNOW how the night went. Inventing "green"
        // there would be the exact silent-success lie this feature exists to kill; the missing heartbeat
        // is what covers it.
        outcome.Should().Be(SentinelOutcome.ProbeFailed);
        mailer.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_failed_result_from_the_health_query_sends_nothing()
    {
        var mailer = new FakeMailer();

        var outcome = await Build(new FakeProbe((PipelineHealth_GetDto?)null), mailer,
                new VirtualTimeProvider(CheckUtc))
            .RunNightlyCheckAsync(CancellationToken.None);

        outcome.Should().Be(SentinelOutcome.ProbeFailed);
        mailer.Sent.Should().BeEmpty();
    }

    // ---- the message ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_alert_carries_the_detail_fields_and_the_log_link()
    {
        var mailer = new FakeMailer();
        var sentinel = Build(
            new FakeProbe(Snapshot(PipelineHealthStates.GateBlocked)), mailer,
            new VirtualTimeProvider(CheckUtc));

        await sentinel.RunNightlyCheckAsync(CancellationToken.None);

        var body = mailer.Sent[0].Body;
        body.Should().Contain("Quality check:          Fail");            // the UI's phrase, not "Verification"
        body.Should().Contain("6f9619ff-8b86-d011-b42d-00cf4fc964ff");   // batch id
        body.Should().Contain("2026-07-26 15:32:10Z");                   // startedUtc
        body.Should().Contain("http://localhost:30080/admin/logs/ingestion");
        // An absent feature build is a FINDING, so the line is present and explicitly empty rather than
        // dropped to make the mail look tidy.
        body.Should().Contain("Feature build:          (none)");
    }

    // ---- the loop around it --------------------------------------------------------------------------

    [Fact]
    public async Task The_hosted_service_idles_when_smtp_is_not_configured()
    {
        var probe = new FakeProbe(Snapshot(PipelineHealthStates.Failed));
        var mailer = new FakeMailer { IsConfigured = false };
        var clock = new VirtualTimeProvider(CheckUtc);
        var settings = new TestSentinelSettings();

        var service = new PipelineSentinelHostedService(
            Build(probe, mailer, clock, settings), mailer, settings, Schedule, clock,
            NullLogger<PipelineSentinelHostedService>.Instance);

        // Starting and stopping must be uneventful: an API with no mailbox configured still boots and
        // serves, it simply cannot alert.
        await service.StartAsync(CancellationToken.None);
        await service.ExecuteTask!;
        await service.StopAsync(CancellationToken.None);

        probe.Calls.Should().Be(0);
        clock.Waits.Should().BeEmpty();
    }

    [Fact]
    public async Task The_hosted_service_sleeps_until_the_configured_check_time_then_runs_one_check()
    {
        // 06:00Z on the 26th is 11:30 Colombo, eleven hours before the 22:30 check.
        var clock = new VirtualTimeProvider(new DateTime(2026, 7, 26, 6, 0, 0, DateTimeKind.Utc));
        var probe = new FakeProbe(Snapshot(PipelineHealthStates.Failed));
        var settings = new TestSentinelSettings();
        using var cts = new CancellationTokenSource();

        // Stop the loop after the first send, so this test observes exactly one nightly cycle.
        var mailer = new FakeMailer { OnSend = () => cts.Cancel() };

        var service = new PipelineSentinelHostedService(
            Build(probe, mailer, clock, settings), mailer, settings, Schedule, clock,
            NullLogger<PipelineSentinelHostedService>.Instance);

        await service.StartAsync(cts.Token);
        await service.ExecuteTask!;

        clock.Waits.Should().ContainSingle().Which.Should().Be(TimeSpan.FromHours(11));
        probe.Calls.Should().Be(1);
        mailer.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task A_restart_after_tonights_check_time_skips_tonight_and_arms_for_tomorrow()
    {
        // 17:05Z is 22:35 Colombo — the pod came back five minutes after tonight's slot.
        var clock = new VirtualTimeProvider(new DateTime(2026, 7, 26, 17, 5, 0, DateTimeKind.Utc));
        var settings = new TestSentinelSettings();
        using var cts = new CancellationTokenSource();
        var probe = new FakeProbe(Snapshot(PipelineHealthStates.Failed)) { Clock = clock };
        var mailer = new FakeMailer { OnSend = () => cts.Cancel() };

        var service = new PipelineSentinelHostedService(
            Build(probe, mailer, clock, settings), mailer, settings, Schedule, clock,
            NullLogger<PipelineSentinelHostedService>.Instance);

        await service.StartAsync(cts.Token);
        await service.ExecuteTask!;

        // Tonight is NOT retro-fired: the sentinel sleeps 23h55m and reads for the first time at
        // TOMORROW's 22:30. A missed night is a real gap, and the startup log line carrying the resolved
        // next-check instant is what lets an operator tell this apart from a broken sentinel.
        clock.Waits.Should().ContainSingle().Which.Should().Be(new TimeSpan(23, 55, 0));
        probe.Instants.Should().ContainSingle()
            .Which.Should().Be(new DateTime(2026, 7, 27, 17, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task A_black_holed_smtp_server_does_not_stop_the_NEXT_night_from_being_checked()
    {
        // The regression this guards: SmtpClient.Timeout does not bound SendMailAsync, so before the
        // send got its own deadline an SMTP endpoint that accepted the connection and never replied
        // parked RunNightlyCheckAsync forever. The loop would never reach its next wait and EVERY
        // subsequent night would go unchecked in silence — a worse outage than the one being alerted on.
        using var blackhole = new BlackholeSmtpServer();
        var mailer = BlackholeMailer(blackhole.Port);
        var clock = new VirtualTimeProvider(new DateTime(2026, 7, 26, 6, 0, 0, DateTimeKind.Utc));
        var settings = new TestSentinelSettings();
        using var cts = new CancellationTokenSource();
        var probe = new FakeProbe(Snapshot(PipelineHealthStates.Failed))
        {
            Clock = clock, StopAfter = cts, StopAfterCalls = 2
        };

        var service = new PipelineSentinelHostedService(
            Build(probe, mailer, clock, settings), mailer, settings, Schedule, clock,
            NullLogger<PipelineSentinelHostedService>.Instance);

        await service.StartAsync(cts.Token);
        await service.ExecuteTask!;

        // Night one hung on the send and was cut loose by the deadline; night two still happened.
        probe.Instants.Should().Equal(
            new DateTime(2026, 7, 26, 17, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 27, 17, 0, 0, DateTimeKind.Utc));
        clock.Waits.Should().Equal(TimeSpan.FromHours(11), TimeSpan.FromHours(24));
    }

    [Fact]
    public void The_next_check_instant_is_the_configured_local_time_strictly_in_the_future()
    {
        var tz = Schedule.ScheduleTimeZone;
        var checkTime = new TimeOnly(22, 30);

        // Mid-morning Colombo: later today.
        PipelineScheduleClock.ResolveNextOccurrenceUtc(
                new DateTime(2026, 7, 26, 6, 0, 0, DateTimeKind.Utc), tz, checkTime)
            .Should().Be(new DateTime(2026, 7, 26, 17, 0, 0, DateTimeKind.Utc));

        // Exactly ON the check time: tomorrow, never "now". An at-or-after comparison here would make
        // the hosted service spin instead of sleeping a day.
        PipelineScheduleClock.ResolveNextOccurrenceUtc(CheckUtc, tz, checkTime)
            .Should().Be(new DateTime(2026, 7, 27, 17, 0, 0, DateTimeKind.Utc));
    }

    // ---- configuration -------------------------------------------------------------------------------

    [Fact]
    public void Sentinel_settings_fall_back_to_the_documented_defaults()
    {
        var settings = new SentinelSettings(new ConfigurationBuilder().Build());

        settings.LocalCheckTime.Should().Be(new TimeOnly(22, 30));
        settings.RunningRecheckMinutes.Should().Be(30);
        settings.AdminLogsUrl.Should().Be("http://localhost:30080/admin/logs/ingestion");
        // Defaults ON: an alert-only sentinel is indistinguishable from a broken one.
        settings.SendGreenHeartbeat.Should().BeTrue();
    }

    [Fact]
    public void Garbage_in_the_sentinel_section_falls_back_instead_of_throwing()
    {
        var settings = new SentinelSettings(Config(new()
        {
            ["Sentinel:LocalCheckTime"] = "half past ten",
            ["Sentinel:RunningRecheckMinutes"] = "-5",
            ["Sentinel:SendGreenHeartbeat"] = "yes please"
        }));

        settings.LocalCheckTime.Should().Be(new TimeOnly(22, 30));
        settings.RunningRecheckMinutes.Should().Be(30);
        settings.SendGreenHeartbeat.Should().BeTrue();
    }

    [Theory]
    // Complete -> armed.
    [InlineData("smtp.gmail.com", "owner@example.com", "owner@example.com", "app-password", true)]
    // Any missing piece -> disabled, never a half-configured client that throws at 22:30.
    [InlineData("smtp.gmail.com", "owner@example.com", "owner@example.com", "", false)]
    [InlineData("smtp.gmail.com", "", "owner@example.com", "app-password", false)]
    [InlineData("smtp.gmail.com", "owner@example.com", "", "app-password", false)]
    // Host is the ONE field with a working default (smtp.gmail.com), so leaving it blank is a valid
    // configuration, not a missing one — the owner only has to supply the account, recipient and app
    // password.
    [InlineData("", "owner@example.com", "owner@example.com", "app-password", true)]
    public void The_smtp_mailer_reports_configured_only_when_every_field_is_present(
        string host, string user, string to, string password, bool expected)
    {
        // Constructed ONLY to read the flag — nothing here opens a socket or sends a message.
        var mailer = new SmtpSentinelMailer(Config(new()
        {
            ["Smtp:Host"] = host,
            ["Smtp:User"] = user,
            ["Smtp:To"] = to,
            ["Smtp:Password"] = password
        }));

        mailer.IsConfigured.Should().Be(expected);
    }

    [Fact]
    public async Task The_smtp_send_is_bounded_by_its_own_deadline_not_by_SmtpClient_Timeout()
    {
        // A server that accepts the socket and never sends the 220 greeting. SmtpClient.Timeout is
        // documented as having NO effect on SendMailAsync, so without the linked CancelAfter this await
        // never returns.
        using var blackhole = new BlackholeSmtpServer();
        var mailer = BlackholeMailer(blackhole.Port);
        var elapsed = Stopwatch.StartNew();

        var send = mailer.SendAsync(new SentinelEmail("subject", "body"), CancellationToken.None);
        var finished = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(20)));

        // Asserted as a race rather than by awaiting, so a regression FAILS here instead of hanging the
        // whole suite until the runner gives up.
        ReferenceEquals(finished, send).Should()
            .BeTrue("a send with no deadline of its own would park the nightly loop forever");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));

        var act = () => send;
        (await act.Should().ThrowAsync<Exception>())
            .Which.Message.Should().NotContain(BlackholePassword);
    }

    [Fact]
    public async Task An_unconfigured_smtp_mailer_refuses_to_send_and_names_only_config_keys()
    {
        var mailer = new SmtpSentinelMailer(new ConfigurationBuilder().Build());

        var act = () => mailer.SendAsync(new SentinelEmail("s", "b"));

        // Fails loudly rather than silently pretending to send, and the message carries key NAMES only —
        // a secret must never reach a log line.
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Smtp:Password").And.NotContain("app-password");
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private const string BlackholePassword = "placeholder-never-used";

    // A REAL SmtpSentinelMailer aimed at the silent local socket, with a 1-second deadline so the tests
    // that use it cost about a second. Nothing here can reach a real mail server: the host is loopback
    // and the port is one this test opened.
    private static SmtpSentinelMailer BlackholeMailer(int port) =>
        new(Config(new()
        {
            ["Smtp:Host"] = "127.0.0.1",
            ["Smtp:Port"] = port.ToString(),
            ["Smtp:SendTimeoutSeconds"] = "1",
            ["Smtp:User"] = "sentinel@invalid.test",
            ["Smtp:To"] = "sentinel@invalid.test",
            ["Smtp:Password"] = BlackholePassword
        }));
}
