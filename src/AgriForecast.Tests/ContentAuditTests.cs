using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Crop.Commands.Create;
using AgriForecast.Application.Requests.Crop.Commands.Delete;
using AgriForecast.Application.Requests.Crop.Commands.Update;
using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Application.Requests.FestivalCalendar.Commands.Create;
using AgriForecast.Application.Requests.FestivalCalendar.Commands.Delete;
using AgriForecast.Application.Requests.FestivalCalendar.Commands.Update;
using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using AgriForecast.Application.Requests.Market.Commands.Create;
using AgriForecast.Application.Requests.Market.DTOs;
using AgriForecast.Application.Requests.NewsEvents.Commands.Create;
using AgriForecast.Application.Requests.NewsEvents.Commands.Delete;
using AgriForecast.Application.Requests.NewsEvents.Commands.Update;
using AgriForecast.Application.Requests.NewsEvents.DTOs;
using AgriForecast.Application.Requests.PolicyFlag.Commands.Create;
using AgriForecast.Application.Requests.PolicyFlag.Commands.Delete;
using AgriForecast.Application.Requests.PolicyFlag.Commands.Update;
using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.Repositories;
using FluentAssertions;
using MarketEntity = AgriForecast.Domain.Entities.Market;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PolicyFlagEntity = AgriForecast.Domain.Entities.PolicyFlag;

namespace AgriForecast.Tests;

/// <summary>
/// The admin CONTENT audit trail: every admin mutation of policy flags, festivals, news events,
/// crops and markets must leave a UserActivityLog row naming the acting admin and what was touched.
///
/// Two guarantees are pinned here, per call site:
///   (1) RECORDED — the handler calls the right Record*ChangedAsync with the acting admin, the right
///       verb, and a short identifier the admin would recognise (title / festival key+date / crop
///       code / market name). A missing call is a silent hole in the trail: the change still happens,
///       nothing says who did it.
///   (2) FAIL-OPEN — an audit write that BLOWS UP must not fail the mutation, which already
///       committed. That guarantee lives in UserActivityAudit (own scope, swallow-and-log), so it is
///       proved here by running a handler against the REAL writer pointed at a database with no
///       UserActivityLog table — not against a mock that politely returns.
///
/// Details format ("created 'X'") is asserted through the real writer rather than at each call site,
/// so the thirteen sites cannot drift into thirteen wordings.
/// </summary>
public class ContentAuditTests
{
    // The acting admin the controller stamps from the JWT sub claim.
    private static readonly Guid ActingAdmin = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static Mock<IUnitofWorkRepository> Uow()
    {
        var uow = new Mock<IUnitofWorkRepository>();
        uow.Setup(u => u.CommitAsync()).Returns(Task.CompletedTask);
        return uow;
    }

    // ── (1) RECORDED: policy flags ────────────────────────────────────────────────────────

    private static PolicyFlagEntity Flag(string title = "GARLIC-IMPORT-BAN") => new()
    {
        Id = Guid.NewGuid(),
        PolicyType = PolicyType.ImportBan,
        Title = title,
        EffectiveFrom = new DateTime(2026, 1, 1),
        Direction = PolicyDirection.Bullish
    };

    [Fact]
    public async Task PolicyFlagCreate_RecordsCreatedWithTitle_AndActingAdmin()
    {
        var repo = new Mock<IPolicyFlagRepository>();
        var audit = new Mock<IUserActivityAudit>();
        var handler = new PolicyFlagCreateCommandHandler(
            repo.Object, Mock.Of<ILogger<PolicyFlagCreateCommandHandler>>(), Uow().Object, audit.Object);

        var result = await handler.Handle(new PolicyFlagCreateCommand
        {
            ActingUserId = ActingAdmin,
            PolicyFlagCreateDto = new PolicyFlag_CreateDto
            {
                PolicyType = PolicyType.ImportBan,
                Title = "GARLIC-IMPORT-BAN",
                EffectiveFrom = new DateTime(2026, 1, 1),
                Direction = PolicyDirection.Bullish
            }
        }, default);

        result.IsSuccess.Should().BeTrue();
        audit.Verify(a => a.RecordPolicyFlagChangedAsync(
            ActingAdmin, ContentChangeAction.Created, "GARLIC-IMPORT-BAN", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PolicyFlagUpdate_RecordsUpdatedWithTitle()
    {
        var existing = Flag();
        var repo = new Mock<IPolicyFlagRepository>();
        repo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        var audit = new Mock<IUserActivityAudit>();
        var handler = new PolicyFlagUpdateCommandHandler(
            repo.Object, Mock.Of<ILogger<PolicyFlagUpdateCommandHandler>>(), Uow().Object, audit.Object);

        var result = await handler.Handle(new PolicyFlagUpdateCommand
        {
            ActingUserId = ActingAdmin,
            PolicyFlagUpdateDto = new PolicyFlag_UpdateDto
            {
                Id = existing.Id,
                PolicyType = PolicyType.ImportBan,
                Title = "GARLIC-IMPORT-BAN-2027",
                EffectiveFrom = new DateTime(2027, 1, 1),
                Direction = PolicyDirection.Bullish
            }
        }, default);

        result.IsSuccess.Should().BeTrue();
        // The identifier is read AFTER ApplyTo, so it names the flag as it now stands.
        audit.Verify(a => a.RecordPolicyFlagChangedAsync(
            ActingAdmin, ContentChangeAction.Updated, "GARLIC-IMPORT-BAN-2027", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PolicyFlagDelete_RecordsDeletedWithTitle()
    {
        var existing = Flag("SUGAR-TAX");
        var repo = new Mock<IPolicyFlagRepository>();
        repo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        var audit = new Mock<IUserActivityAudit>();
        var handler = new PolicyFlagDeleteCommandHandler(
            repo.Object, Mock.Of<ILogger<PolicyFlagDeleteCommandHandler>>(), Uow().Object, audit.Object);

        var result = await handler.Handle(new PolicyFlagDeleteCommand(existing.Id, ActingAdmin), default);

        result.IsSuccess.Should().BeTrue();
        audit.Verify(a => a.RecordPolicyFlagChangedAsync(
            ActingAdmin, ContentChangeAction.Deleted, "SUGAR-TAX", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PolicyFlagDelete_NotFound_RecordsNothing()
    {
        var repo = new Mock<IPolicyFlagRepository>();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((PolicyFlagEntity?)null);
        var audit = new Mock<IUserActivityAudit>();
        var handler = new PolicyFlagDeleteCommandHandler(
            repo.Object, Mock.Of<ILogger<PolicyFlagDeleteCommandHandler>>(), Uow().Object, audit.Object);

        var result = await handler.Handle(new PolicyFlagDeleteCommand(Guid.NewGuid(), ActingAdmin), default);

        result.IsSuccess.Should().BeFalse();
        // Nothing was committed, so nothing may be recorded — an audit trail that logs attempts as
        // if they were changes is worse than none.
        audit.VerifyNoOtherCalls();
    }

    // ── (1) RECORDED: festivals ───────────────────────────────────────────────────────────

    private static FestivalCalendarEntry Festival(string key = "VESAK", int year = 2027) => new()
    {
        Id = Guid.NewGuid(),
        FestivalKey = key,
        Date = new DateTime(year, 5, 10),
        LeadUpDays = 14,
        Source = "seed"
    };

    private static Mock<IFestivalCalendarRepository> FestivalRepo(FestivalCalendarEntry? existing = null)
    {
        var repo = new Mock<IFestivalCalendarRepository>();
        repo.Setup(r => r.ExistsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<Guid?>()))
            .ReturnsAsync(false);
        if (existing is not null)
            repo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        return repo;
    }

    [Fact]
    public async Task FestivalCreate_RecordsCreatedWithKeyAndDate()
    {
        var audit = new Mock<IUserActivityAudit>();
        var handler = new FestivalCalendarCreateCommandHandler(
            FestivalRepo().Object, Mock.Of<ILogger<FestivalCalendarCreateCommandHandler>>(),
            Uow().Object, audit.Object);

        var result = await handler.Handle(new FestivalCalendarCreateCommand
        {
            ActingUserId = ActingAdmin,
            FestivalCalendarCreateDto = new FestivalCalendar_CreateDto
            {
                FestivalKey = "VESAK",
                Date = new DateTime(2027, 5, 10),
                LeadUpDays = 14,
                Source = "almanac"
            }
        }, default);

        result.IsSuccess.Should().BeTrue();
        // Key AND date: the same festival recurs yearly, so the key alone would not say WHICH one.
        audit.Verify(a => a.RecordFestivalChangedAsync(
            ActingAdmin, ContentChangeAction.Created, "VESAK 2027-05-10", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FestivalUpdate_RecordsUpdatedWithKeyAndDate()
    {
        var existing = Festival();
        var audit = new Mock<IUserActivityAudit>();
        var handler = new FestivalCalendarUpdateCommandHandler(
            FestivalRepo(existing).Object, Mock.Of<ILogger<FestivalCalendarUpdateCommandHandler>>(),
            Uow().Object, audit.Object);

        var result = await handler.Handle(new FestivalCalendarUpdateCommand
        {
            ActingUserId = ActingAdmin,
            FestivalCalendarUpdateDto = new FestivalCalendar_UpdateDto
            {
                Id = existing.Id,
                FestivalKey = "VESAK",
                Date = new DateTime(2027, 5, 11), // moved a day
                LeadUpDays = 14,
                Source = "almanac"
            }
        }, default);

        result.IsSuccess.Should().BeTrue();
        audit.Verify(a => a.RecordFestivalChangedAsync(
            ActingAdmin, ContentChangeAction.Updated, "VESAK 2027-05-11", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FestivalDelete_RecordsDeletedWithKeyAndDate()
    {
        var existing = Festival("POSON", 2026);
        var audit = new Mock<IUserActivityAudit>();
        var handler = new FestivalCalendarDeleteCommandHandler(
            FestivalRepo(existing).Object, Mock.Of<ILogger<FestivalCalendarDeleteCommandHandler>>(),
            Uow().Object, audit.Object);

        var result = await handler.Handle(
            new FestivalCalendarDeleteCommand(existing.Id, ActingAdmin), default);

        result.IsSuccess.Should().BeTrue();
        audit.Verify(a => a.RecordFestivalChangedAsync(
            ActingAdmin, ContentChangeAction.Deleted, "POSON 2026-05-10", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── (1) RECORDED: news events ─────────────────────────────────────────────────────────

    private static NewsEvent News(string title = "Onion import restrictions announced") => new()
    {
        Id = Guid.NewGuid(),
        EventType = NewsEventType.ImportBan,
        Direction = PolicyDirection.Bullish,
        Title = title,
        PublishedAt = new DateTime(2026, 7, 10)
    };

    private static Mock<INewsEventRepository> NewsRepo(NewsEvent? existing = null)
    {
        var repo = new Mock<INewsEventRepository>();
        repo.Setup(r => r.CropsExistAsync(It.IsAny<IReadOnlyCollection<Guid>?>())).ReturnsAsync(true);
        repo.Setup(r => r.MarketsExistAsync(It.IsAny<IReadOnlyCollection<Guid>?>())).ReturnsAsync(true);
        if (existing is not null)
            repo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        return repo;
    }

    [Fact]
    public async Task NewsEventCreate_RecordsCreatedWithTitle()
    {
        var audit = new Mock<IUserActivityAudit>();
        var handler = new NewsEventCreateCommandHandler(
            NewsRepo().Object, Mock.Of<ILogger<NewsEventCreateCommandHandler>>(), Uow().Object, audit.Object);

        var result = await handler.Handle(new NewsEventCreateCommand
        {
            ActingUserId = ActingAdmin,
            NewsEventCreateDto = new NewsEvent_CreateDto
            {
                EventType = NewsEventType.ImportBan,
                Direction = PolicyDirection.Bullish,
                Title = "Onion import restrictions announced",
                PublishedAt = new DateTime(2026, 7, 10)
            }
        }, default);

        result.IsSuccess.Should().BeTrue();
        audit.Verify(a => a.RecordNewsEventChangedAsync(
            ActingAdmin, ContentChangeAction.Created, "Onion import restrictions announced",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NewsEventUpdate_RecordsUpdatedWithTitle()
    {
        var existing = News();
        var audit = new Mock<IUserActivityAudit>();
        var handler = new NewsEventUpdateCommandHandler(
            NewsRepo(existing).Object, Mock.Of<ILogger<NewsEventUpdateCommandHandler>>(),
            Uow().Object, audit.Object);

        var result = await handler.Handle(new NewsEventUpdateCommand
        {
            ActingUserId = ActingAdmin,
            NewsEventUpdateDto = new NewsEvent_UpdateDto
            {
                Id = existing.Id,
                EventType = NewsEventType.ExportBan,
                Direction = PolicyDirection.Bearish,
                Title = "Export ban lifted",
                AffectedCropIds = new List<Guid>(),
                AffectedMarketIds = new List<Guid>()
            }
        }, default);

        result.IsSuccess.Should().BeTrue();
        audit.Verify(a => a.RecordNewsEventChangedAsync(
            ActingAdmin, ContentChangeAction.Updated, "Export ban lifted", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task NewsEventDelete_RecordsDeletedWithTitle()
    {
        var existing = News("Fuel price revision");
        var audit = new Mock<IUserActivityAudit>();
        var handler = new NewsEventDeleteCommandHandler(
            NewsRepo(existing).Object, Mock.Of<ILogger<NewsEventDeleteCommandHandler>>(),
            Uow().Object, audit.Object);

        var result = await handler.Handle(new NewsEventDeleteCommand(existing.Id, ActingAdmin), default);

        result.IsSuccess.Should().BeTrue();
        audit.Verify(a => a.RecordNewsEventChangedAsync(
            ActingAdmin, ContentChangeAction.Deleted, "Fuel price revision", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── (1) RECORDED: crops ───────────────────────────────────────────────────────────────

    private static CodeSettings Codes()
    {
        var settingRepo = new Mock<IDefaultSettingRepository>();
        settingRepo.Setup(r => r.GetDefaultSetting()).ReturnsAsync(new DefaultSetting
        {
            Id = 1,
            Veg_Prefix = "VEG", Veg_Padding = 6, Veg_Code = 71,
            Frt_Prefix = "FRT", Frt_Padding = 6, Frt_Code = 27,
            Mkt_Prefix = "MKT", Mkt_Padding = 8, Mkt_Code = 7
        });
        return new CodeSettings(settingRepo.Object);
    }

    [Fact]
    public async Task CropCreate_RecordsCreatedWithGeneratedCropCode()
    {
        var audit = new Mock<IUserActivityAudit>();
        var handler = new CropCreateCommandHandler(
            Codes(), Uow().Object, Mock.Of<ILogger<CropCreateCommandHandler>>(),
            Mock.Of<ICropRepository>(), Mock.Of<IGenericRepository<CropAgronomyProfile>>(), audit.Object);

        var result = await handler.Handle(new CropCreateCommand
        {
            ActingUserId = ActingAdmin,
            CreateDto = new Crop_CreateDto { Name = "Beetroot", CropCategoryId = Guid.NewGuid() }
        }, default);

        result.IsSuccess.Should().BeTrue();
        // The CropCode the admin will see in the list, not the raw GUID.
        audit.Verify(a => a.RecordCropChangedAsync(
            ActingAdmin, ContentChangeAction.Created, "VEG000071", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CropUpdate_RecordsUpdatedWithCropCode()
    {
        var existing = Crop.CreateFromExternalSource("Carrot", "manual", "VEG000012");
        var repo = new Mock<ICropRepository>();
        repo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        var audit = new Mock<IUserActivityAudit>();
        var handler = new CropUpdateCommandHandler(
            repo.Object, Uow().Object, Mock.Of<ILogger<CropUpdateCommandHandler>>(), audit.Object);

        var result = await handler.Handle(new CropUpdateCommand
        {
            ActingUserId = ActingAdmin,
            CropUpdateDto = new Crop_UpdateDto { Id = existing.Id, Name = "Carrot (Local)" }
        }, default);

        result.IsSuccess.Should().BeTrue();
        audit.Verify(a => a.RecordCropChangedAsync(
            ActingAdmin, ContentChangeAction.Updated, "VEG000012", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CropDelete_RecordsDeletedWithCropCode()
    {
        var existing = Crop.CreateFromExternalSource("Leeks", "manual", "VEG000071");
        var repo = new Mock<ICropRepository>();
        repo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        var audit = new Mock<IUserActivityAudit>();
        var handler = new CropDeleteCommandHandler(
            repo.Object, Uow().Object, Mock.Of<ILogger<CropDeleteCommandHandler>>(), audit.Object);

        var result = await handler.Handle(new CropDeleteCommand(existing.Id, ActingAdmin), default);

        result.IsSuccess.Should().BeTrue();
        audit.Verify(a => a.RecordCropChangedAsync(
            ActingAdmin, ContentChangeAction.Deleted, "VEG000071", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── (1) RECORDED: markets ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task MarketCreate_RecordsCreatedWithMarketName()
    {
        var audit = new Mock<IUserActivityAudit>();
        var handler = new MarketCreateCommandHandler(
            Codes(), Mock.Of<IGenericRepository<MarketEntity>>(), Uow().Object,
            Mock.Of<ILogger<MarketCreateCommandHandler>>(), audit.Object);

        var result = await handler.Handle(new MarketCreateCommand
        {
            ActingUserId = ActingAdmin,
            CreateDto = new Market_CreateDto
            {
                Name = "Nuwara Eliya DEC",
                District = "Nuwara Eliya",
                MarketType = MarketType.DEC,
                IsEconomicCenter = true
            }
        }, default);

        result.IsSuccess.Should().BeTrue();
        audit.Verify(a => a.RecordMarketChangedAsync(
            ActingAdmin, ContentChangeAction.Created, "Nuwara Eliya DEC", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── (2) FAIL-OPEN: a broken audit write never fails the committed mutation ────────────

    // The REAL writer against a database with NO UserActivityLog table: every write throws inside
    // UserActivityAudit and must be swallowed there. A mock that returns cleanly would prove nothing.
    private static async Task<(SqliteConnection conn, ServiceProvider provider, UserActivityAudit audit)>
        BrokenAuditAsync()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        var services = new ServiceCollection();
        services.AddDbContext<AgriForecastDbContext>(o => o.UseSqlite(connection));
        var provider = services.BuildServiceProvider();
        var audit = new UserActivityAudit(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<UserActivityAudit>.Instance);
        return (connection, provider, audit);
    }

    [Fact]
    public async Task PolicyFlagCreate_StillSucceeds_WhenTheAuditWriteBlowsUp()
    {
        var (conn, provider, audit) = await BrokenAuditAsync();
        await using var _c = conn;
        await using var _p = provider;

        var uow = Uow();
        var handler = new PolicyFlagCreateCommandHandler(
            Mock.Of<IPolicyFlagRepository>(), Mock.Of<ILogger<PolicyFlagCreateCommandHandler>>(),
            uow.Object, audit);

        var result = await handler.Handle(new PolicyFlagCreateCommand
        {
            ActingUserId = ActingAdmin,
            PolicyFlagCreateDto = new PolicyFlag_CreateDto
            {
                PolicyType = PolicyType.ImportBan,
                Title = "GARLIC-IMPORT-BAN",
                EffectiveFrom = new DateTime(2026, 1, 1),
                Direction = PolicyDirection.Bullish
            }
        }, default);

        result.IsSuccess.Should().BeTrue("the flag was already committed — the audit is not a gate");
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task CropDelete_StillSucceeds_WhenTheAuditWriteBlowsUp()
    {
        var (conn, provider, audit) = await BrokenAuditAsync();
        await using var _c = conn;
        await using var _p = provider;

        var existing = Crop.CreateFromExternalSource("Leeks", "manual", "VEG000071");
        var repo = new Mock<ICropRepository>();
        repo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        var uow = Uow();
        var handler = new CropDeleteCommandHandler(
            repo.Object, uow.Object, Mock.Of<ILogger<CropDeleteCommandHandler>>(), audit);

        var result = await handler.Handle(new CropDeleteCommand(existing.Id, ActingAdmin), default);

        result.IsSuccess.Should().BeTrue();
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task EveryContentRecordMethod_SwallowsAWriteFailure()
    {
        var (conn, provider, audit) = await BrokenAuditAsync();
        await using var _c = conn;
        await using var _p = provider;

        // One assertion per entity kind: none of the five may surface a DB failure to its caller.
        var writes = new Func<Task>[]
        {
            () => audit.RecordPolicyFlagChangedAsync(ActingAdmin, ContentChangeAction.Created, "x"),
            () => audit.RecordFestivalChangedAsync(ActingAdmin, ContentChangeAction.Updated, "x"),
            () => audit.RecordNewsEventChangedAsync(ActingAdmin, ContentChangeAction.Deleted, "x"),
            () => audit.RecordCropChangedAsync(ActingAdmin, ContentChangeAction.Created, "x"),
            () => audit.RecordMarketChangedAsync(ActingAdmin, ContentChangeAction.Created, "x")
        };

        foreach (var write in writes)
            await write.Should().NotThrowAsync();
    }

    // ── Details rendering + row shape (the one place the wording is decided) ──────────────

    [Theory]
    [InlineData(ContentChangeAction.Created, "Vesak festival 2027", "created 'Vesak festival 2027'")]
    [InlineData(ContentChangeAction.Updated, "GARLIC-IMPORT-BAN", "updated 'GARLIC-IMPORT-BAN'")]
    [InlineData(ContentChangeAction.Deleted, "VEG000071", "deleted 'VEG000071'")]
    public void RenderDetails_IsVerbThenQuotedIdentifier(
        ContentChangeAction action, string identifier, string expected)
    {
        UserActivityAudit.RenderDetails(action, identifier).Should().Be(expected);
    }

    [Fact]
    public void RenderDetails_BlankIdentifier_IsTheBareVerb_NotEmptyQuotes()
    {
        UserActivityAudit.RenderDetails(ContentChangeAction.Deleted, "   ").Should().Be("deleted");
        UserActivityAudit.RenderDetails(ContentChangeAction.Deleted, null).Should().Be("deleted");
    }

    [Fact]
    public void RenderDetails_OverlongIdentifier_IsCappedWellUnderTheDetailsColumn()
    {
        var note = UserActivityAudit.RenderDetails(ContentChangeAction.Created, new string('t', 400));

        note.Length.Should().BeLessThan(500, "Details is nvarchar(500) and an overflow would fail the write");
        note.Should().StartWith("created '");
    }

    [Fact]
    public void ContentRow_CarriesTheActorAndDetails_ButNoTargetUserOrUsername()
    {
        var row = UserActivityEvent.CropChanged(ActingAdmin, "created 'VEG000071'", DateTime.UtcNow);

        row.EventType.Should().Be(UserActivityEventType.CropChanged);
        row.ActorUserId.Should().Be(ActingAdmin);
        row.TargetUserId.Should().BeNull("content events act on content, not on a user");
        row.UsernameAttempted.Should().BeNull();
        row.Details.Should().Be("created 'VEG000071'");
    }

    [Fact]
    public void ContentRow_EmptyActor_IsStoredAsNull_NotAnAllZerosGuid()
    {
        var row = UserActivityEvent.PolicyFlagChanged(Guid.Empty, "created 'x'", DateTime.UtcNow);

        row.ActorUserId.Should().BeNull(
            "an unattributable change is recorded honestly, never credited to a fabricated user id");
        row.Details.Should().Be("created 'x'", "the change itself is still recorded");
    }
}
