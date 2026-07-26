using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.NewsEvents.Commands.Create;
using AgriForecast.Application.Requests.NewsEvents.Commands.Delete;
using AgriForecast.Application.Requests.NewsEvents.Commands.Update;
using AgriForecast.Application.Requests.NewsEvents.DTOs;
using AgriForecast.Application.Requests.NewsEvents.Quaries.GetAll;
using AgriForecast.Application.Requests.NewsEvents.Validators;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// News-event (API-12) vertical slice — CAPTURE + STORAGE ONLY (NewsEvents are not yet ML feature
/// inputs, so — unlike PolicyFlag / FestivalCalendar — there is no training-data-warning idiom).
/// Pins: enum-int mapping fidelity, the validator matrix (title / eventType / direction / sourceUrl
/// / publishedAt-required-on-create), PublishedAt IMMUTABILITY on update (the UpdateDto omits it and
/// the handler preserves the stored vintage), empty read = Success + empty list, and crop/market
/// link round-trip. Style mirrors FestivalCalendarTests.
/// </summary>
public class NewsEventTests
{
    // The acting admin the controller would stamp from the JWT; fixed so audit assertions can
    // name the exact actor rather than matching any Guid.
    private static readonly Guid ActingAdmin = Guid.Parse("11111111-2222-3333-4444-555555555555");

    // A repo whose existence checks pass, for validator/handler happy paths.
    private static Mock<INewsEventRepository> PermissiveRepo()
    {
        var repo = new Mock<INewsEventRepository>();
        repo.Setup(r => r.CropsExistAsync(It.IsAny<IReadOnlyCollection<Guid>?>())).ReturnsAsync(true);
        repo.Setup(r => r.MarketsExistAsync(It.IsAny<IReadOnlyCollection<Guid>?>())).ReturnsAsync(true);
        return repo;
    }

    private NewsEventCreateCommandValidator CreateValidator(Mock<INewsEventRepository>? repo = null)
        => new((repo ?? PermissiveRepo()).Object);

    private NewsEventUpdateCommandValidator UpdateValidator(Mock<INewsEventRepository>? repo = null)
        => new((repo ?? PermissiveRepo()).Object);

    private static NewsEventCreateCommand ValidCreate() => new()
    {
        NewsEventCreateDto = new NewsEvent_CreateDto
        {
            EventType = NewsEventType.ImportBan,
            Direction = PolicyDirection.Bullish,
            Title = "Onion import restrictions announced",
            Description = "Government restricts onion imports for the season.",
            PublishedAt = new DateTime(2026, 07, 10),
            SourceUrl = "https://news.example.lk/onion-import-ban",
            AffectedCropIds = new List<Guid> { Guid.NewGuid() },
            AffectedMarketIds = new List<Guid> { Guid.NewGuid() }
        }
    };

    private static NewsEventUpdateCommand ValidUpdate() => new()
    {
        NewsEventUpdateDto = new NewsEvent_UpdateDto
        {
            Id = Guid.NewGuid(),
            EventType = NewsEventType.ExportBan,
            Direction = PolicyDirection.Bearish,
            Title = "Export ban lifted",
            Description = null,
            SourceUrl = null,
            AffectedCropIds = new List<Guid>(),
            AffectedMarketIds = new List<Guid>()
        }
    };

    // ──────────────────────────────────────────────────────────────────────────────
    // Mapping fidelity (enum ints + links round-trip)
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToEntity_PreservesEnumIntsAndDateOnly_AndBuildsLinks()
    {
        var cropId = Guid.NewGuid();
        var marketId = Guid.NewGuid();
        var dto = new NewsEvent_CreateDto
        {
            EventType = NewsEventType.Budget,           // 8
            Direction = PolicyDirection.Bearish,        // -1
            Title = "Interim budget",
            PublishedAt = new DateTime(2026, 07, 10, 14, 30, 0), // time component must be dropped
            AffectedCropIds = new List<Guid> { cropId, cropId }, // duplicate collapses
            AffectedMarketIds = new List<Guid> { marketId }
        };

        var entity = dto.ToEntity();

        ((int)entity.EventType).Should().Be(8);
        ((int)entity.Direction).Should().Be(-1);
        entity.PublishedAt.Should().Be(new DateTime(2026, 07, 10)); // date-only
        entity.PublishedAt.TimeOfDay.Should().Be(TimeSpan.Zero);
        entity.Id.Should().NotBe(Guid.Empty);
        entity.AffectedCrops.Should().HaveCount(1);                 // de-duplicated
        entity.AffectedCrops.Single().CropId.Should().Be(cropId);
        entity.AffectedCrops.Single().NewsEventId.Should().Be(entity.Id);
        entity.AffectedMarkets.Single().MarketId.Should().Be(marketId);
    }

    [Fact]
    public void GetDto_RoundTripsEnumIntsAndFlattensLinks()
    {
        var id = Guid.NewGuid();
        var cropId = Guid.NewGuid();
        var marketId = Guid.NewGuid();
        var entity = new NewsEvent
        {
            Id = id,
            EventType = NewsEventType.FuelPriceChange, // 6
            Direction = PolicyDirection.Neutral,       // 0
            Title = "Fuel price revision",
            PublishedAt = new DateTime(2026, 06, 01),
            CreatedAtUtc = DateTime.UtcNow,
            AffectedCrops = new List<NewsEventCrop> { new() { NewsEventId = id, CropId = cropId } },
            AffectedMarkets = new List<NewsEventMarket> { new() { NewsEventId = id, MarketId = marketId } }
        };

        var dto = entity.ToGetDto();

        ((int)dto.EventType).Should().Be(6);
        ((int)dto.Direction).Should().Be(0);
        dto.AffectedCropIds.Should().ContainSingle().Which.Should().Be(cropId);
        dto.AffectedMarketIds.Should().ContainSingle().Which.Should().Be(marketId);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Create validator matrix
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_ValidCommand_Passes()
        => (await CreateValidator().ValidateAsync(ValidCreate())).IsValid.Should().BeTrue();

    [Fact]
    public async Task Create_MissingTitle_Fails()
    {
        var cmd = ValidCreate();
        cmd.NewsEventCreateDto.Title = "";
        var r = await CreateValidator().ValidateAsync(cmd);
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName.Contains("Title"));
    }

    [Fact]
    public async Task Create_UndefinedEventType_Fails()
    {
        var cmd = ValidCreate();
        cmd.NewsEventCreateDto.EventType = (NewsEventType)99;
        var r = await CreateValidator().ValidateAsync(cmd);
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName.Contains("EventType"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Create_DirectionWhitelist_Passes(int dir)
    {
        var cmd = ValidCreate();
        cmd.NewsEventCreateDto.Direction = (PolicyDirection)dir;
        (await CreateValidator().ValidateAsync(cmd)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-2)]
    public async Task Create_DirectionOutOfWhitelist_Fails(int dir)
    {
        var cmd = ValidCreate();
        cmd.NewsEventCreateDto.Direction = (PolicyDirection)dir;
        var r = await CreateValidator().ValidateAsync(cmd);
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName.Contains("Direction"));
    }

    [Fact]
    public async Task Create_MissingPublishedAt_Fails()
    {
        var cmd = ValidCreate();
        cmd.NewsEventCreateDto.PublishedAt = default;
        var r = await CreateValidator().ValidateAsync(cmd);
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName.Contains("PublishedAt"));
    }

    [Fact]
    public async Task Create_NoSourceUrl_Passes()
    {
        // SourceUrl is optional — absent is valid.
        var cmd = ValidCreate();
        cmd.NewsEventCreateDto.SourceUrl = null;
        (await CreateValidator().ValidateAsync(cmd)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/x")]   // wrong scheme
    [InlineData("/relative/path")]        // not absolute
    [InlineData("example.com")]           // no scheme
    public async Task Create_MalformedSourceUrl_Fails(string url)
    {
        var cmd = ValidCreate();
        cmd.NewsEventCreateDto.SourceUrl = url;
        var r = await CreateValidator().ValidateAsync(cmd);
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName.Contains("SourceUrl"));
    }

    [Theory]
    [InlineData("http://example.lk/a")]
    [InlineData("https://news.example.lk/path?q=1")]
    public async Task Create_WellFormedHttpUrl_Passes(string url)
    {
        var cmd = ValidCreate();
        cmd.NewsEventCreateDto.SourceUrl = url;
        (await CreateValidator().ValidateAsync(cmd)).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Create_UnknownCropLink_Fails()
    {
        var repo = PermissiveRepo();
        repo.Setup(r => r.CropsExistAsync(It.IsAny<IReadOnlyCollection<Guid>?>())).ReturnsAsync(false);
        var r = await CreateValidator(repo).ValidateAsync(ValidCreate());
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName.Contains("AffectedCropIds"));
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Update validator
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_ValidCommand_Passes()
        => (await UpdateValidator().ValidateAsync(ValidUpdate())).IsValid.Should().BeTrue();

    [Fact]
    public async Task Update_MissingId_Fails()
    {
        var cmd = ValidUpdate();
        cmd.NewsEventUpdateDto.Id = Guid.Empty;
        var r = await UpdateValidator().ValidateAsync(cmd);
        r.IsValid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.PropertyName.Contains("Id"));
    }

    [Fact]
    public void UpdateDto_HasNoPublishedAtProperty()
    {
        // Immutability by construction: the UpdateDto must NOT carry PublishedAt at all, so the
        // vintage cannot be rewritten over the wire.
        typeof(NewsEvent_UpdateDto).GetProperty("PublishedAt").Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Handlers
    // ──────────────────────────────────────────────────────────────────────────────

    private static Mock<IUnitofWorkRepository> Uow() => new();

    [Fact]
    public async Task CreateHandler_Happy_AddsAndCommits()
    {
        var repo = PermissiveRepo();
        var uow = Uow();
        var handler = new NewsEventCreateCommandHandler(
            repo.Object, Mock.Of<ILogger<NewsEventCreateCommandHandler>>(), uow.Object, Mock.Of<IUserActivityAudit>());

        var result = await handler.Handle(ValidCreate(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<NewsEvent>()), Times.Once);
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task UpdateHandler_NotFound_Fails()
    {
        var repo = PermissiveRepo();
        repo.Setup(r => r.GetByIdAsync(It.IsAny<Guid>())).ReturnsAsync((NewsEvent?)null);
        var uow = Uow();
        var handler = new NewsEventUpdateCommandHandler(
            repo.Object, Mock.Of<ILogger<NewsEventUpdateCommandHandler>>(), uow.Object, Mock.Of<IUserActivityAudit>());

        var result = await handler.Handle(ValidUpdate(), default);

        result.IsSuccess.Should().BeFalse();
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task UpdateHandler_PreservesStoredPublishedAt_AndReconcilesLinks()
    {
        // The stored (vintage) PublishedAt must survive an update untouched — the UpdateDto never
        // carries it and the mapper never writes it.
        var storedPublishedAt = new DateTime(2025, 01, 15);
        var existing = new NewsEvent
        {
            Id = Guid.NewGuid(),
            EventType = NewsEventType.Other,
            Direction = PolicyDirection.Neutral,
            Title = "old title",
            PublishedAt = storedPublishedAt,
            CreatedAtUtc = new DateTime(2025, 01, 16, 8, 0, 0, DateTimeKind.Utc)
        };

        var repo = PermissiveRepo();
        repo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        var uow = Uow();

        var cmd = ValidUpdate();
        cmd.NewsEventUpdateDto.Id = existing.Id;
        cmd.NewsEventUpdateDto.Title = "new title";

        var handler = new NewsEventUpdateCommandHandler(
            repo.Object, Mock.Of<ILogger<NewsEventUpdateCommandHandler>>(), uow.Object, Mock.Of<IUserActivityAudit>());

        var result = await handler.Handle(cmd, default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(existing.Id);
        existing.Title.Should().Be("new title");             // scalar field applied
        existing.PublishedAt.Should().Be(storedPublishedAt); // vintage preserved
        repo.Verify(r => r.SetCropLinks(existing, cmd.NewsEventUpdateDto.AffectedCropIds), Times.Once);
        repo.Verify(r => r.SetMarketLinks(existing, cmd.NewsEventUpdateDto.AffectedMarketIds), Times.Once);
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    [Fact]
    public async Task DeleteHandler_EmptyId_Fails()
    {
        var repo = PermissiveRepo();
        var uow = Uow();
        var handler = new NewsEventDeleteCommandHandler(
            repo.Object, Mock.Of<ILogger<NewsEventDeleteCommandHandler>>(), uow.Object, Mock.Of<IUserActivityAudit>());

        var result = await handler.Handle(new NewsEventDeleteCommand(Guid.Empty, ActingAdmin), default);

        result.IsSuccess.Should().BeFalse();
        repo.Verify(r => r.GetByIdAsync(It.IsAny<Guid>()), Times.Never);
        uow.Verify(u => u.CommitAsync(), Times.Never);
    }

    [Fact]
    public async Task DeleteHandler_Happy_DeletesAndCommits()
    {
        var existing = new NewsEvent { Id = Guid.NewGuid(), Title = "x", PublishedAt = new DateTime(2026, 01, 01) };
        var repo = PermissiveRepo();
        repo.Setup(r => r.GetByIdAsync(existing.Id)).ReturnsAsync(existing);
        var uow = Uow();
        var handler = new NewsEventDeleteCommandHandler(
            repo.Object, Mock.Of<ILogger<NewsEventDeleteCommandHandler>>(), uow.Object, Mock.Of<IUserActivityAudit>());

        var result = await handler.Handle(new NewsEventDeleteCommand(existing.Id, ActingAdmin), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(existing.Id);
        repo.Verify(r => r.DeleteAsync(existing), Times.Once);
        uow.Verify(u => u.CommitAsync(), Times.Once);
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // GetAll query handler
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAll_ReturnsMappedEvents()
    {
        var repo = new Mock<INewsEventRepository>();
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(new[]
        {
            new NewsEvent { Id = Guid.NewGuid(), Title = "a", PublishedAt = new DateTime(2026, 07, 10) },
            new NewsEvent { Id = Guid.NewGuid(), Title = "b", PublishedAt = new DateTime(2026, 06, 01) }
        });

        var handler = new NewsEventGetAllQueryHandler(repo.Object, Mock.Of<ILogger<NewsEventGetAllQueryHandler>>());
        var result = await handler.Handle(new NewsEventGetAllQuery(), default);

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAll_Empty_ReturnsSuccessWithEmptyList()
    {
        var repo = new Mock<INewsEventRepository>();
        repo.Setup(r => r.GetAllAsync()).ReturnsAsync(Array.Empty<NewsEvent>());

        var handler = new NewsEventGetAllQueryHandler(repo.Object, Mock.Of<ILogger<NewsEventGetAllQueryHandler>>());
        var result = await handler.Handle(new NewsEventGetAllQuery(), default);

        // Empty list is a normal state → 200 [] on the wire, never the legacy policy-flag
        // 400-on-empty quirk.
        result.IsSuccess.Should().BeTrue();
        result.Data.Should().BeEmpty();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // Link reconcile (repository) — diff semantics, never clear-and-re-add
    // ──────────────────────────────────────────────────────────────────────────────
    // SetCropLinks/SetMarketLinks run against a TRACKED entity graph. If a retained
    // link were removed and re-added as a fresh instance, EF's identity map would hold
    // two instances with the same composite key → tracking exception (500) on a routine
    // update. The load-bearing property is therefore reference preservation: a retained
    // link must keep its ORIGINAL instance. The methods touch only the entity (no
    // DbContext use), so they are unit-tested directly.

    private static NewsEventRepository ReconcileRepo() => new(null!);

    [Fact]
    public void SetCropLinks_RetainedLinkKeepsOriginalInstance_RemovedGone_AddedNew()
    {
        var evt = new NewsEvent { Id = Guid.NewGuid(), Title = "t", PublishedAt = new DateTime(2026, 7, 1) };
        var keepId = Guid.NewGuid();
        var dropId = Guid.NewGuid();
        var addId = Guid.NewGuid();
        var kept = new NewsEventCrop { NewsEventId = evt.Id, CropId = keepId };
        evt.AffectedCrops.Add(kept);
        evt.AffectedCrops.Add(new NewsEventCrop { NewsEventId = evt.Id, CropId = dropId });

        ReconcileRepo().SetCropLinks(evt, new[] { keepId, addId });

        evt.AffectedCrops.Should().HaveCount(2);
        evt.AffectedCrops.Select(l => l.CropId).Should().BeEquivalentTo(new[] { keepId, addId });
        // Retained link is the SAME tracked instance — this is what prevents the
        // EF same-key-already-tracked failure that clear-and-re-add would cause.
        ReferenceEquals(evt.AffectedCrops.Single(l => l.CropId == keepId), kept).Should().BeTrue();
    }

    [Fact]
    public void SetCropLinks_NullOrEmpty_RemovesAllLinks_AndDupesCollapse()
    {
        var evt = new NewsEvent { Id = Guid.NewGuid(), Title = "t", PublishedAt = new DateTime(2026, 7, 1) };
        evt.AffectedCrops.Add(new NewsEventCrop { NewsEventId = evt.Id, CropId = Guid.NewGuid() });

        ReconcileRepo().SetCropLinks(evt, null);
        evt.AffectedCrops.Should().BeEmpty();

        var dupe = Guid.NewGuid();
        ReconcileRepo().SetCropLinks(evt, new[] { dupe, dupe });
        evt.AffectedCrops.Should().ContainSingle().Which.CropId.Should().Be(dupe);
    }

    [Fact]
    public void SetMarketLinks_RetainedLinkKeepsOriginalInstance()
    {
        var evt = new NewsEvent { Id = Guid.NewGuid(), Title = "t", PublishedAt = new DateTime(2026, 7, 1) };
        var keepId = Guid.NewGuid();
        var kept = new NewsEventMarket { NewsEventId = evt.Id, MarketId = keepId };
        evt.AffectedMarkets.Add(kept);
        evt.AffectedMarkets.Add(new NewsEventMarket { NewsEventId = evt.Id, MarketId = Guid.NewGuid() });

        ReconcileRepo().SetMarketLinks(evt, new[] { keepId });

        evt.AffectedMarkets.Should().ContainSingle();
        ReferenceEquals(evt.AffectedMarkets.Single(), kept).Should().BeTrue();
    }
}
