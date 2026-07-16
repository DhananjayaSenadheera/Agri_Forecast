using AgriForecast.Application.Requests.FestivalCalendar;
using AgriForecast.Application.Requests.FestivalCalendar.Commands.Create;
using AgriForecast.Application.Requests.FestivalCalendar.Commands.Delete;
using AgriForecast.Application.Requests.FestivalCalendar.Commands.Update;
using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using AgriForecast.Application.Requests.FestivalCalendar.Quaries.GetAll;
using AgriForecast.Application.Requests.FestivalCalendar.Validators;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Festival-calendar (API-10) vertical slice. The ML model TRAINS on this table, so the tests pin
/// the rules that protect the feature layer:
///   - Create/Update validators: FestivalKey uppercase-key guard, date-only, LeadUpDays >= 0 with
///     the PAIRED-DAY convention (0 is valid — a multi-day festival's continuation day), Source
///     required on every mutation.
///   - FestivalCalendarTrainingDataWarning: past-dated mutation warns (delete + update, stored vs
///     incoming); future-dated does not.
///   - Handlers: happy paths, not-found, empty-id, duplicate (FestivalKey, Date) guard, GetAll.
/// Style mirrors PolicyFlagTests.
/// </summary>
public class FestivalCalendarTests
{
    // ──────────────────────────────────────────────────────────────────────────────
    // Create validator
    // ──────────────────────────────────────────────────────────────────────────────

    private readonly FestivalCalendarCreateCommandValidator _createValidator = new();
    private readonly FestivalCalendarUpdateCommandValidator _updateValidator = new();

    private static FestivalCalendarCreateCommand ValidCreate() => new()
    {
        FestivalCalendarCreateDto = new FestivalCalendar_CreateDto
        {
            FestivalKey = "AVURUDU",
            Date = new DateTime(2026, 04, 13),
            LeadUpDays = 14,
            IsProvisional = false,
            Source = "Department of Government Printing gazette 2026"
        }
    };

    [Fact]
    public async Task Create_ValidCommand_Passes()
    {
        (await _createValidator.ValidateAsync(ValidCreate())).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Create_MissingFestivalKey_Fails()
    {
        var cmd = ValidCreate();
        cmd.FestivalCalendarCreateDto.FestivalKey = "";
        var result = await _createValidator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("FestivalKey"));
    }

    [Theory]
    [InlineData("avurudu")]      // lowercase — ML per-festival match is case-sensitive
    [InlineData("Thai Pongal")]  // space
    [InlineData("AVURUDU!")]     // punctuation
    public async Task Create_NonUppercaseKeyFormat_Fails(string key)
    {
        var cmd = ValidCreate();
        cmd.FestivalCalendarCreateDto.FestivalKey = key;
        var result = await _createValidator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("FestivalKey"));
    }

    [Theory]
    [InlineData("AVURUDU")]
    [InlineData("THAI_PONGAL")]
    [InlineData("VESAK2")]
    public async Task Create_UppercaseKeyFormats_Pass(string key)
    {
        var cmd = ValidCreate();
        cmd.FestivalCalendarCreateDto.FestivalKey = key;
        (await _createValidator.ValidateAsync(cmd)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Create_MissingDate_Fails()
    {
        var cmd = ValidCreate();
        cmd.FestivalCalendarCreateDto.Date = default;
        var result = await _createValidator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Date"));
    }

    [Fact]
    public async Task Create_LeadUpDaysZero_Passes()
    {
        // PAIRED-DAY convention: a multi-day festival's continuation row carries LeadUpDays=0 so the
        // lead-up window anchored on the eve is not double-counted. 0 MUST be accepted.
        var cmd = ValidCreate();
        cmd.FestivalCalendarCreateDto.LeadUpDays = 0;
        (await _createValidator.ValidateAsync(cmd)).IsValid
            .Should().BeTrue("LeadUpDays=0 is the paired-day continuation convention");
    }

    [Fact]
    public async Task Create_NegativeLeadUpDays_Fails()
    {
        var cmd = ValidCreate();
        cmd.FestivalCalendarCreateDto.LeadUpDays = -1;
        var result = await _createValidator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("LeadUpDays"));
    }

    [Fact]
    public async Task Create_LeadUpDaysAboveCap_Fails()
    {
        var cmd = ValidCreate();
        cmd.FestivalCalendarCreateDto.LeadUpDays = 91;
        var result = await _createValidator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("LeadUpDays"));
    }

    [Fact]
    public async Task Create_MissingSource_Fails()
    {
        // Source is REQUIRED on create (festivals are curated data) — stricter than PolicyFlag.
        var cmd = ValidCreate();
        cmd.FestivalCalendarCreateDto.Source = "";
        var result = await _createValidator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Source"));
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Update validator
    // ──────────────────────────────────────────────────────────────────────────────

    private static FestivalCalendarUpdateCommand ValidUpdate() => new()
    {
        FestivalCalendarUpdateDto = new FestivalCalendar_UpdateDto
        {
            Id = Guid.NewGuid(),
            FestivalKey = "CHRISTMAS",
            Date = new DateTime(2026, 12, 25),
            LeadUpDays = 14,
            IsProvisional = false,
            Source = "Fixed Gregorian date"
        }
    };

    [Fact]
    public async Task Update_ValidCommand_Passes()
    {
        (await _updateValidator.ValidateAsync(ValidUpdate())).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Update_MissingId_Fails()
    {
        var cmd = ValidUpdate();
        cmd.FestivalCalendarUpdateDto.Id = Guid.Empty;
        var result = await _updateValidator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Id"));
    }

    [Fact]
    public async Task Update_MissingSource_Fails()
    {
        var cmd = ValidUpdate();
        cmd.FestivalCalendarUpdateDto.Source = "";
        var result = await _updateValidator.ValidateAsync(cmd);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName.Contains("Source"));
    }

    [Fact]
    public async Task Update_LeadUpDaysZero_Passes()
    {
        var cmd = ValidUpdate();
        cmd.FestivalCalendarUpdateDto.LeadUpDays = 0;
        (await _updateValidator.ValidateAsync(cmd)).IsValid.Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Training-data warning helper
    // ──────────────────────────────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 07, 16, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Warning_PastDate_Warns()
    {
        FestivalCalendarTrainingDataWarning.For(new DateTime(2026, 04, 13), null, Now)
            .Should().NotBeNull();
    }

    [Fact]
    public void Warning_FutureDate_NoWarning()
    {
        FestivalCalendarTrainingDataWarning.For(new DateTime(2026, 12, 25), null, Now)
            .Should().BeNull();
    }

    [Fact]
    public void Warning_Today_NoWarning()
    {
        // "Past" is STRICTLY before today; today itself has not yet been trained on.
        FestivalCalendarTrainingDataWarning.For(Now.Date, null, Now).Should().BeNull();
    }

    [Fact]
    public void Warning_Update_StoredPastIncomingFuture_Warns()
    {
        // Moving a festival OUT of the past still rewrites history — the previous (stored) date warns.
        FestivalCalendarTrainingDataWarning
            .For(new DateTime(2026, 12, 25), new DateTime(2026, 01, 14), Now)
            .Should().NotBeNull();
    }

    [Fact]
    public void Warning_Update_StoredFutureIncomingPast_Warns()
    {
        // Moving a festival INTO the past rewrites history — the incoming date warns.
        FestivalCalendarTrainingDataWarning
            .For(new DateTime(2026, 01, 14), new DateTime(2026, 12, 25), Now)
            .Should().NotBeNull();
    }

    [Fact]
    public void Warning_Update_BothFuture_NoWarning()
    {
        FestivalCalendarTrainingDataWarning
            .For(new DateTime(2026, 12, 25), new DateTime(2026, 11, 01), Now)
            .Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Create handler
    // ──────────────────────────────────────────────────────────────────────────────

    private static (Mock<IFestivalCalendarRepository>, Mock<IUnitofWorkRepository>) Mocks()
        => (new Mock<IFestivalCalendarRepository>(), new Mock<IUnitofWorkRepository>());

    [Fact]
    public async Task CreateHandler_Happy_AddsAndCommits()
    {
        var (repo, uow) = Mocks();
        repo.Setup(r => r.ExistsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), null)).ReturnsAsync(false);

        var handler = new FestivalCalendarCreateCommandHandler(
            repo.Object, Mock.Of<ILogger<FestivalCalendarCreateCommandHandler>>(), uow.Object);

        var result = await handler.Handle(ValidCreate(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<FestivalCalendarEntry>()), Times.Once);
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task CreateHandler_DuplicateKeyDate_FailsWithoutInsert()
    {
        var (repo, uow) = Mocks();
        repo.Setup(r => r.ExistsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), null)).ReturnsAsync(true);

        var handler = new FestivalCalendarCreateCommandHandler(
            repo.Object, Mock.Of<ILogger<FestivalCalendarCreateCommandHandler>>(), uow.Object);

        var result = await handler.Handle(ValidCreate(), default);

        result.IsSuccess.Should().BeFalse();
        repo.Verify(r => r.AddAsync(It.IsAny<FestivalCalendarEntry>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Update handler
    // ──────────────────────────────────────────────────────────────────────────────

    private static FestivalCalendarEntry Entry(DateTime date, string key = "AVURUDU") => new()
    {
        Id = Guid.NewGuid(),
        FestivalKey = key,
        Date = date,
        LeadUpDays = 14,
        IsProvisional = false,
        Source = "seed",
        CreatedAtUtc = DateTime.UtcNow
    };

    [Fact]
    public async Task UpdateHandler_NotFound_Fails()
    {
        var (repo, uow) = Mocks();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((FestivalCalendarEntry?)null);

        var handler = new FestivalCalendarUpdateCommandHandler(
            repo.Object, Mock.Of<ILogger<FestivalCalendarUpdateCommandHandler>>(), uow.Object);

        var result = await handler.Handle(ValidUpdate(), default);

        result.IsSuccess.Should().BeFalse();
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task UpdateHandler_FutureDate_SucceedsNoWarning()
    {
        var (repo, uow) = Mocks();
        var existing = Entry(new DateTime(2030, 12, 25));
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(existing);
        repo.Setup(r => r.ExistsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<Guid?>()))
            .ReturnsAsync(false);

        var cmd = ValidUpdate();
        cmd.FestivalCalendarUpdateDto.Date = new DateTime(2030, 12, 25);

        var handler = new FestivalCalendarUpdateCommandHandler(
            repo.Object, Mock.Of<ILogger<FestivalCalendarUpdateCommandHandler>>(), uow.Object);

        var result = await handler.Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.TrainingDataWarning.Should().BeNull();
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateHandler_PastStoredDate_SucceedsWithWarning()
    {
        var (repo, uow) = Mocks();
        var existing = Entry(new DateTime(2020, 04, 13));   // stored date is in the past
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(existing);
        repo.Setup(r => r.ExistsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<Guid?>()))
            .ReturnsAsync(false);

        var cmd = ValidUpdate();
        cmd.FestivalCalendarUpdateDto.Date = new DateTime(2030, 12, 25); // moving out of the past

        var handler = new FestivalCalendarUpdateCommandHandler(
            repo.Object, Mock.Of<ILogger<FestivalCalendarUpdateCommandHandler>>(), uow.Object);

        var result = await handler.Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.TrainingDataWarning.Should().NotBeNull();
        result.Warnings.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task UpdateHandler_DuplicateTarget_FailsWithoutCommit()
    {
        var (repo, uow) = Mocks();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(Entry(new DateTime(2030, 12, 25)));
        repo.Setup(r => r.ExistsAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<Guid?>()))
            .ReturnsAsync(true);

        var handler = new FestivalCalendarUpdateCommandHandler(
            repo.Object, Mock.Of<ILogger<FestivalCalendarUpdateCommandHandler>>(), uow.Object);

        var result = await handler.Handle(ValidUpdate(), default);

        result.IsSuccess.Should().BeFalse();
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Delete handler
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteHandler_EmptyId_Fails()
    {
        var (repo, uow) = Mocks();
        var handler = new FestivalCalendarDeleteCommandHandler(
            repo.Object, Mock.Of<ILogger<FestivalCalendarDeleteCommandHandler>>(), uow.Object);

        var result = await handler.Handle(new FestivalCalendarDeleteCommand(Guid.Empty), default);

        result.IsSuccess.Should().BeFalse();
        repo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task DeleteHandler_NotFound_Fails()
    {
        var (repo, uow) = Mocks();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((FestivalCalendarEntry?)null);

        var handler = new FestivalCalendarDeleteCommandHandler(
            repo.Object, Mock.Of<ILogger<FestivalCalendarDeleteCommandHandler>>(), uow.Object);

        var result = await handler.Handle(new FestivalCalendarDeleteCommand(Guid.NewGuid()), default);

        result.IsSuccess.Should().BeFalse();
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task DeleteHandler_PastDate_SucceedsWithWarning()
    {
        var (repo, uow) = Mocks();
        var existing = Entry(new DateTime(2020, 04, 13));
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(existing);

        var handler = new FestivalCalendarDeleteCommandHandler(
            repo.Object, Mock.Of<ILogger<FestivalCalendarDeleteCommandHandler>>(), uow.Object);

        var result = await handler.Handle(new FestivalCalendarDeleteCommand(existing.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Id.Should().Be(existing.Id);
        result.Data.TrainingDataWarning.Should().NotBeNull();
        repo.Verify(r => r.DeleteAsync(existing), Times.Once);
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task DeleteHandler_FutureDate_SucceedsNoWarning()
    {
        var (repo, uow) = Mocks();
        var existing = Entry(new DateTime(2030, 12, 25));
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync(existing);

        var handler = new FestivalCalendarDeleteCommandHandler(
            repo.Object, Mock.Of<ILogger<FestivalCalendarDeleteCommandHandler>>(), uow.Object);

        var result = await handler.Handle(new FestivalCalendarDeleteCommand(existing.Id), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.TrainingDataWarning.Should().BeNull();
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // GetAll query handler
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_ReturnsMappedEntries()
    {
        var repo = new Mock<IFestivalCalendarRepository>();
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(new[]
        {
            Entry(new DateTime(2026, 04, 13)),
            Entry(new DateTime(2026, 12, 25), "CHRISTMAS")
        });

        var handler = new FestivalCalendarGetAllQueryHandler(
            repo.Object, Mock.Of<ILogger<FestivalCalendarGetAllQueryHandler>>());

        var result = await handler.Handle(new FestivalCalendarGetAllQuery(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAll_Empty_ReturnsSuccessWithEmptyList()
    {
        var repo = new Mock<IFestivalCalendarRepository>();
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(Array.Empty<FestivalCalendarEntry>());

        var handler = new FestivalCalendarGetAllQueryHandler(
            repo.Object, Mock.Of<ILogger<FestivalCalendarGetAllQueryHandler>>());

        var result = await handler.Handle(new FestivalCalendarGetAllQuery(), default);

        // Empty calendar is a normal state → 200 [] on the wire, never the
        // legacy policy-flag 400-on-empty quirk.
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }
}
