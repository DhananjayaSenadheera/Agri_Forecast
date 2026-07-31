using AgriForecast.Application.Requests.Portfolio.Commands.DeleteSale;
using AgriForecast.Application.Requests.Portfolio.Commands.RecordSale;
using AgriForecast.Application.Requests.Portfolio.Commands.UpdateSale;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.Queries.GetSales;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Unit tests for the sales-log handlers. The database is faked (a user-scoped sales repository and a read
/// store projected from the SAME row list), so what is covered here is everything that decides what a
/// farmer sees, is allowed to touch, and is told when they mis-type.
/// <para>
/// CROSS-USER ISOLATION IS THE FIRST-CLASS TARGET, more so than anywhere else in the product: these rows
/// are the farmer's own sale prices. The fakes hold rows for TWO users and honour the user filter exactly
/// as the real repository and store do, so a handler that forgot to scope shows up here as another
/// farmer's sale in a response — not as a passing test against a single-user fixture. A foreign row answers
/// 404, never 403, and never a different error from the one an unknown id gets.
/// </para>
/// <para>
/// The second target is the PINNED 400 CODE FAMILY and its ORDER. Every code is asserted as a LITERAL
/// STRING, because these are wire values the UI switches on: comparing them to the constants that produce
/// them would still pass if somebody renamed a value.
/// </para>
/// Style mirrors PortfolioHandlerTests.cs.
/// </summary>
public class SalesHandlerTests
{
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UserB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static readonly Guid Carrot = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid Tomato = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid UnknownCrop = Guid.Parse("c0000000-0000-0000-0000-0000000000ff");

    private static readonly Guid Dambulla = Guid.Parse("b2a20001-0000-0000-0000-000000000001");
    private static readonly Guid Keppetipola = Guid.Parse("b2a20001-0000-0000-0000-000000000002");
    private static readonly Guid UnknownMarket = Guid.Parse("b2a20001-0000-0000-0000-0000000000ff");

    private static readonly DateTime Seeded = new(2026, 7, 20, 6, 0, 0, DateTimeKind.Utc);

    // A date that is safely in the past whatever the clock says when the suite runs.
    private static string PastDay(int daysBack = 3)
        => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-daysBack).ToString("yyyy-MM-dd");

    // xUnit's InlineData cannot carry a decimal? (it hands the runner an int, which will not convert), so
    // the money cases travel as strings and are parsed here — invariant, so the test data reads the same
    // on any machine.
    private static decimal? Money(string? value)
        => value is null ? null : decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    // Fake sales repository + unit of work. Holds rows for EVERY user and filters by the id it is asked
    // for, exactly like the real repository's WHERE clause — so an unscoped handler fails here.
    private sealed class FakeSales : IUserSaleRepository, IUnitofWorkRepository
    {
        public readonly List<UserSale> Rows = new();
        public int CommitCount { get; private set; }

        public Task<UserSale?> GetForUserAsync(Guid userId, Guid saleId, CancellationToken ct = default)
            => Task.FromResult(Rows.FirstOrDefault(r => r.UserId == userId && r.Id == saleId));

        public Task AddAsync(UserSale sale, CancellationToken ct = default)
        {
            Rows.Add(sale);
            return Task.CompletedTask;
        }

        public void Remove(UserSale sale) => Rows.Remove(sale);

        public Task CommitAsync()
        {
            CommitCount++;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    // Fake read store. The sales projection is derived from the SAME row list the repository holds, so a
    // write made by a handler is visible to the read-back without a second fixture to keep in step. Only
    // the members the sales handlers actually use are implemented; the rest fail loudly.
    private sealed class FakeStore : IPortfolioReadStore
    {
        public readonly FakeSales Sales = new();

        // The audit spy. It captures the commit count AT CALL TIME, because "the audit runs AFTER the
        // commit" is an ordering claim and an unordered mock cannot make it.
        public readonly Mock<IUserActivityAudit> AuditMock = new();

        public readonly List<(UserActivityEventType Type, Guid Actor, string? Details, int CommitCountAtCall)>
            Audited = new();

        public readonly Dictionary<Guid, (string Name, string? Code)> Crops = new();
        public readonly Dictionary<Guid, PortfolioMarketRow> Markets = new();

        // Every user id the sales reads were issued for — a handler that read with somebody else's id (or
        // with none) is visible here.
        public readonly List<Guid> CapturedSalesUserIds = new();

        public FakeStore()
        {
            // One setup per verb, written out rather than looped: Moq parses the expression tree, so a
            // helper that took the method as a delegate would not be interceptable.
            AuditMock
                .Setup(a => a.RecordSaleRecordedAsync(
                    It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback((Guid actor, string? details, CancellationToken _) =>
                    Audited.Add((UserActivityEventType.SaleRecorded, actor, details, Sales.CommitCount)))
                .Returns(Task.CompletedTask);

            AuditMock
                .Setup(a => a.RecordSaleUpdatedAsync(
                    It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback((Guid actor, string? details, CancellationToken _) =>
                    Audited.Add((UserActivityEventType.SaleUpdated, actor, details, Sales.CommitCount)))
                .Returns(Task.CompletedTask);

            AuditMock
                .Setup(a => a.RecordSaleDeletedAsync(
                    It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Callback((Guid actor, string? details, CancellationToken _) =>
                    Audited.Add((UserActivityEventType.SaleDeleted, actor, details, Sales.CommitCount)))
                .Returns(Task.CompletedTask);
        }

        public void AddCrop(Guid id, string name, string? code = null) => Crops[id] = (name, code);

        public void AddMarket(Guid id, string name, string shortCode)
            => Markets[id] = new PortfolioMarketRow(id, name, shortCode, false);

        private UserSaleRow Project(UserSale s)
        {
            var crop = Crops.TryGetValue(s.CropId, out var c) ? c : ("?", null);
            PortfolioMarketRow? market = s.MarketId.HasValue && Markets.TryGetValue(s.MarketId.Value, out var m)
                ? m
                : null;

            return new UserSaleRow(
                s.Id, s.CropId, crop.Item1, crop.Item2,
                s.MarketId, market?.Name, market?.ShortCode,
                s.SaleDate, s.PricePerKg, s.QuantityKg, s.Note, s.CreatedAtUtc, s.UpdatedAtUtc);
        }

        public Task<UserSalesPage> GetSalesPageAsync(
            Guid userId, Guid? cropId, int page, int pageSize, CancellationToken ct = default)
        {
            CapturedSalesUserIds.Add(userId);

            var matching = Sales.Rows
                .Where(s => s.UserId == userId)
                .Where(s => !cropId.HasValue || s.CropId == cropId.Value)
                .ToList();

            // The SAME total order the real store's ORDER BY produces.
            var ordered = matching
                .OrderByDescending(s => s.SaleDate)
                .ThenByDescending(s => s.CreatedAtUtc)
                .ThenByDescending(s => s.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(Project)
                .ToList();

            return Task.FromResult(new UserSalesPage(ordered, matching.Count));
        }

        public Task<UserSaleRow?> GetSaleAsync(Guid userId, Guid saleId, CancellationToken ct = default)
        {
            CapturedSalesUserIds.Add(userId);
            var row = Sales.Rows.FirstOrDefault(s => s.UserId == userId && s.Id == saleId);
            return Task.FromResult(row is null ? null : Project(row));
        }

        public Task<bool> CropExistsAsync(Guid cropId, CancellationToken ct = default)
            => Task.FromResult(Crops.ContainsKey(cropId));

        public Task<PortfolioMarketRow?> GetMarketAsync(Guid marketId, CancellationToken ct = default)
            => Task.FromResult(Markets.TryGetValue(marketId, out var m) ? m : null);

        // Not on any sales path. Reaching them from a sales handler would be a bug worth failing for.
        public Task<IReadOnlyList<WatchlistRow>> GetWatchlistAsync(Guid userId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<CropLatestObservation>> GetLatestObservedDatesAsync(
            IReadOnlyCollection<Guid> cropIds, Guid marketId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PortfolioObservationRow>> GetObservationsAsync(
            IReadOnlyCollection<CropObservationWindow> windows, Guid marketId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<PortfolioSnapshotRow>> GetLatestSnapshotsAsync(
            IReadOnlyCollection<Guid> cropIds, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<PortfolioMarketRow?> GetEconomicCentreMarketAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static FakeStore NewStore()
    {
        var store = new FakeStore();
        store.AddCrop(Carrot, "Carrot", "VEG000001");
        store.AddCrop(Tomato, "Tomato", "VEG000002");
        store.AddMarket(Dambulla, "Dambulla Dedicated Economic Centre", "DEC");
        store.AddMarket(Keppetipola, "Keppetipola Dedicated Economic Centre", "KEP");
        return store;
    }

    // Seeds one recorded sale directly, bypassing the handler.
    private static UserSale Seed(
        FakeStore store, Guid userId, Guid cropId, DateOnly saleDate,
        decimal price = 100m, Guid? marketId = null, decimal? quantity = null, string? note = null,
        int createdDayOffset = 0)
    {
        var row = UserSale.Record(
            userId, cropId, marketId, saleDate, price, quantity, note, Seeded.AddDays(createdDayOffset));
        store.Sales.Rows.Add(row);
        return row;
    }

    private static RecordSaleCommandHandler RecordHandler(FakeStore s) =>
        new(s.Sales, s, s.Sales, s.AuditMock.Object, NullLogger<RecordSaleCommandHandler>.Instance);

    private static UpdateSaleCommandHandler UpdateHandler(FakeStore s) =>
        new(s.Sales, s, s.Sales, s.AuditMock.Object, NullLogger<UpdateSaleCommandHandler>.Instance);

    private static DeleteSaleCommandHandler DeleteHandler(FakeStore s) =>
        new(s.Sales, s, s.Sales, s.AuditMock.Object, NullLogger<DeleteSaleCommandHandler>.Instance);

    private static GetSalesQueryHandler SalesQueryHandler(FakeStore s) => new(s);

    // "Not supplied" for saleDate has to be a SENTINEL rather than null, because null is one of the values
    // under test — a `?? PastDay()` default would have quietly turned "no date at all" into a valid one and
    // made the invalid_sale_date case pass for the wrong reason.
    private const string UnsetDate = " unset";

    private static RecordSaleCommand NewSale(
        Guid userId, Guid cropId, string? saleDate = UnsetDate, decimal? price = 155.00m,
        Guid? marketId = null, decimal? quantity = null, string? note = null)
        => new()
        {
            UserId = userId,
            CropId = cropId,
            MarketId = marketId,
            SaleDate = saleDate == UnsetDate ? PastDay() : saleDate,
            PricePerKg = price,
            QuantityKg = quantity,
            Note = note
        };

    // POST /sales — the happy path.

    [Fact]
    public async Task Record_StoresTheSale_AndReturnsItInTheListShape()
    {
        var store = NewStore();
        var day = PastDay();

        var result = await RecordHandler(store).Handle(
            NewSale(UserA, Carrot, day, 155.00m, Dambulla, 42.5m, "  paid in cash  "),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Data.CropId.Should().Be(Carrot);
        result.Data.CropName.Should().Be("Carrot");
        result.Data.CropCode.Should().Be("VEG000001");
        result.Data.MarketId.Should().Be(Dambulla);
        result.Data.MarketName.Should().Be("Dambulla Dedicated Economic Centre");
        result.Data.MarketShortCode.Should().Be("DEC");
        result.Data.SaleDate.Should().Be(day, "dates go out as yyyy-MM-dd strings, never as instants");
        result.Data.PricePerKg.Should().Be(155.00m);
        result.Data.QuantityKg.Should().Be(42.5m);
        result.Data.Note.Should().Be("paid in cash", "the note is stored TRIMMED");
        result.Data.CreatedAtUtc.Kind.Should().Be(DateTimeKind.Utc, "the UI must not read it as local");

        store.Sales.Rows.Should().ContainSingle();
        store.Sales.CommitCount.Should().Be(1, "one write, one transaction");
    }

    [Fact]
    public async Task Record_WithoutTheOptionalFields_IsAccepted()
    {
        var store = NewStore();

        var result = await RecordHandler(store).Handle(
            NewSale(UserA, Carrot), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Data.MarketId.Should().BeNull();
        result.Data.QuantityKg.Should().BeNull();
        result.Data.Note.Should().BeNull();
    }

    [Fact]
    public async Task Record_TwoIdenticalSalesOnTheSameDay_AreBothKept()
    {
        // Deliberately NOT idempotent, unlike the watchlist add: two buyers on one day is an ordinary thing
        // to have happened, and collapsing them would delete a real row of the farmer's own history.
        var store = NewStore();
        var handler = RecordHandler(store);
        var day = PastDay();

        await handler.Handle(NewSale(UserA, Carrot, day, 155m), CancellationToken.None);
        await handler.Handle(NewSale(UserA, Carrot, day, 155m), CancellationToken.None);

        store.Sales.Rows.Should().HaveCount(2);
    }

    // POST /sales — the pinned 400 family. Every code asserted as a LITERAL string.

    [Theory]
    // The price is passed as a STRING and parsed here: xUnit's InlineData cannot carry a decimal? at all
    // (it hands the runner an int, which will not convert), and null is one of the cases under test.
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("-1")]
    [InlineData("-155.50")]
    public async Task Record_MissingOrNonPositivePrice_Is_invalid_price(string? price)
    {
        var store = NewStore();

        var result = await RecordHandler(store).Handle(
            NewSale(UserA, Carrot, price: Money(price)), CancellationToken.None);

        result.Error.Should().Be("invalid_price");
        store.Sales.Rows.Should().BeEmpty("validation runs strictly BEFORE any mutation");
        store.Sales.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Record_PriceCeiling_IsPinnedFromBothSides()
    {
        var store = NewStore();
        var handler = RecordHandler(store);

        // At the ceiling: accepted.
        var atCap = await handler.Handle(
            NewSale(UserA, Carrot, price: SaleLimits.MaxPricePerKg), CancellationToken.None);
        atCap.IsSuccess.Should().BeTrue("the ceiling is INCLUSIVE");

        // One cent over: refused, with its OWN code — "that is far too large" is a different mistake from
        // "that is not a price", and the farmer fixes them differently.
        var overCap = await handler.Handle(
            NewSale(UserA, Carrot, price: SaleLimits.MaxPricePerKg + 0.01m), CancellationToken.None);
        overCap.Error.Should().Be("price_out_of_range");

        SaleLimits.MaxPricePerKg.Should().Be(100_000m, "the ceiling is the wire contract");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("28/07/2026")]
    [InlineData("2026-7-28")]
    [InlineData("2026-13-01")]
    [InlineData("yesterday")]
    [InlineData("2026-07-28T00:00:00Z")]
    public async Task Record_UnparsableDate_Is_invalid_sale_date(string? saleDate)
    {
        var store = NewStore();

        var result = await RecordHandler(store).Handle(
            NewSale(UserA, Carrot, saleDate: saleDate), CancellationToken.None);

        result.Error.Should().Be("invalid_sale_date");
        store.Sales.Rows.Should().BeEmpty();
    }

    [Fact]
    public async Task Record_TodayIsAccepted_AndAFutureDateIs_sale_date_future()
    {
        var store = NewStore();
        var handler = RecordHandler(store);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Today always passes.
        var now = await handler.Handle(
            NewSale(UserA, Carrot, saleDate: today.ToString("yyyy-MM-dd")), CancellationToken.None);
        now.IsSuccess.Should().BeTrue("a farmer recording a sale they made this morning is the normal case");

        // UTC + 1 also passes, and that is DELIBERATE, not slack that crept in: Sri Lanka is UTC+5:30, so
        // between 18:30 and 24:00 UTC a farmer's local "today" is already the next UTC calendar day. This is
        // the SAME rule the planting-date validation uses (PortfolioTime.LatestPlausibleLocalDate) — one
        // clock, so the two surfaces cannot disagree about which day it is.
        var colomboEvening = await handler.Handle(
            NewSale(UserA, Carrot, saleDate: today.AddDays(1).ToString("yyyy-MM-dd")),
            CancellationToken.None);
        colomboEvening.IsSuccess.Should().BeTrue();

        // Two days out is nobody's "today" in any timezone.
        var future = await handler.Handle(
            NewSale(UserA, Carrot, saleDate: today.AddDays(2).ToString("yyyy-MM-dd")),
            CancellationToken.None);
        future.Error.Should().Be("sale_date_future");

        var wayOut = await handler.Handle(
            NewSale(UserA, Carrot, saleDate: today.AddYears(1).ToString("yyyy-MM-dd")),
            CancellationToken.None);
        wayOut.Error.Should().Be("sale_date_future");
    }

    [Fact]
    public void TheFutureDateRuleIsTheSameOneThePlantingDateUses()
    {
        // The mechanism behind the test above, pinned directly so nobody can "fix" one surface's clock and
        // leave the other's behind.
        var noon = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

        PortfolioTime.LatestPlausibleLocalDate(noon).Should().Be(new DateOnly(2026, 7, 31));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("100000.01")]
    public async Task Record_BadQuantity_Is_invalid_quantity(string quantity)
    {
        var store = NewStore();

        var result = await RecordHandler(store).Handle(
            NewSale(UserA, Carrot, quantity: Money(quantity)), CancellationToken.None);

        result.Error.Should().Be("invalid_quantity");
    }

    [Fact]
    public async Task Record_AnAbsentQuantityIsNeverAnError()
    {
        var store = NewStore();

        var result = await RecordHandler(store).Handle(
            NewSale(UserA, Carrot, quantity: null), CancellationToken.None);

        result.IsSuccess.Should().BeTrue("quantity is optional; only a supplied value is checked");
    }

    [Fact]
    public async Task Record_NoteLengthIsMeasuredOnTheTrimmedValue()
    {
        // THE TRIM LAW, with raw and trimmed lengths DISAGREEING so the test cannot pass by accident.
        var store = NewStore();
        var handler = RecordHandler(store);

        // 506 raw characters, 500 after trimming: ACCEPTED, and stored trimmed.
        var padded = "   " + new string('x', UserSale.NoteMaxLength) + "   ";
        padded.Length.Should().Be(UserSale.NoteMaxLength + 6, "the raw value is genuinely over the cap");

        var accepted = await handler.Handle(
            NewSale(UserA, Carrot, note: padded), CancellationToken.None);

        accepted.IsSuccess.Should().BeTrue("a note only over the cap because of padding is not too long");
        accepted.Data.Note.Should().HaveLength(UserSale.NoteMaxLength);

        // 501 characters of actual text: REJECTED, never truncated — silently shortening a farmer's own
        // words is worse than asking them to shorten them.
        var tooLong = await handler.Handle(
            NewSale(UserA, Carrot, note: new string('x', UserSale.NoteMaxLength + 1)),
            CancellationToken.None);

        tooLong.Error.Should().Be("note_too_long");
        store.Sales.Rows.Should().ContainSingle("the rejected note wrote nothing");
    }

    [Fact]
    public async Task Record_UnknownCrop_Is_unknown_crop()
    {
        var store = NewStore();

        var result = await RecordHandler(store).Handle(
            NewSale(UserA, UnknownCrop), CancellationToken.None);

        result.Error.Should().Be("unknown_crop");
        store.Sales.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Record_UnknownMarket_Is_unknown_market()
    {
        var store = NewStore();

        var result = await RecordHandler(store).Handle(
            NewSale(UserA, Carrot, marketId: UnknownMarket), CancellationToken.None);

        result.Error.Should().Be("unknown_market");
        store.Sales.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Record_WhenSeveralRulesFail_ThePayloadRulesAnswerFirst_InAFixedOrder()
    {
        // The order is part of the contract: a UI that highlighted a different box on each retry of the
        // same broken payload would look broken itself. Payload rules (price, date, quantity, note) come
        // before the reference lookups, which cost a query each.
        var store = NewStore();
        var handler = RecordHandler(store);

        var everythingWrong = new RecordSaleCommand
        {
            UserId = UserA,
            CropId = UnknownCrop,
            MarketId = UnknownMarket,
            SaleDate = "not a date",
            PricePerKg = null,
            QuantityKg = -1m,
            Note = new string('x', UserSale.NoteMaxLength + 1)
        };

        (await handler.Handle(everythingWrong, CancellationToken.None)).Error
            .Should().Be("invalid_price", "price is checked first");

        everythingWrong.PricePerKg = 155m;
        (await handler.Handle(everythingWrong, CancellationToken.None)).Error
            .Should().Be("invalid_sale_date", "then the date");

        everythingWrong.SaleDate = PastDay();
        (await handler.Handle(everythingWrong, CancellationToken.None)).Error
            .Should().Be("invalid_quantity", "then the quantity");

        everythingWrong.QuantityKg = 5m;
        (await handler.Handle(everythingWrong, CancellationToken.None)).Error
            .Should().Be("note_too_long", "then the note");

        everythingWrong.Note = null;
        (await handler.Handle(everythingWrong, CancellationToken.None)).Error
            .Should().Be("unknown_crop", "and only then the reference lookups");

        everythingWrong.CropId = Carrot;
        (await handler.Handle(everythingWrong, CancellationToken.None)).Error
            .Should().Be("unknown_market");

        store.Sales.Rows.Should().BeEmpty("not one of those six attempts wrote anything");
    }

    // POST /sales — the audit line.

    [Fact]
    public async Task Record_AuditsAfterTheCommit_WithTheCropCodePriceAndDate()
    {
        var store = NewStore();
        var day = PastDay();

        await RecordHandler(store).Handle(
            NewSale(UserA, Carrot, day, 155.00m, Dambulla, 42.5m, "sold to my neighbour"),
            CancellationToken.None);

        var audited = store.Audited.Should().ContainSingle().Subject;
        audited.Type.Should().Be(UserActivityEventType.SaleRecorded);
        audited.Actor.Should().Be(UserA, "a farmer's sale is authored by the farmer, not an admin");
        audited.Details.Should().Be($"VEG000001 · Rs 155.00/kg · {day}");
        audited.CommitCountAtCall.Should().Be(1,
            "the audit line goes out AFTER the commit — it is fail-open decoration, not part of the write");
    }

    [Fact]
    public async Task Record_TheFarmersNoteNeverReachesTheAuditLine()
    {
        var store = NewStore();

        await RecordHandler(store).Handle(
            NewSale(UserA, Carrot, note: "sold cheap because I owed Kamal money"),
            CancellationToken.None);

        var details = store.Audited.Single().Details!;
        details.Should().NotContain("Kamal");
        details.Should().NotContain("owed");
        details.Should().NotContain("cheap");
    }

    [Fact]
    public async Task Record_TheSaleIsAlreadyCommittedByTheTimeTheAuditIsAttempted()
    {
        // Fail-open in the shape that matters: whatever the audit seam then does with the call (the real
        // UserActivityAudit swallows and logs — see UserSaleTests), the farmer's row is DURABLE first. An
        // audit attempted before the commit could take a saved sale down with it.
        var store = NewStore();

        await RecordHandler(store).Handle(NewSale(UserA, Carrot), CancellationToken.None);

        store.Audited.Should().ContainSingle().Which.CommitCountAtCall.Should().Be(1);
        store.Sales.Rows.Should().ContainSingle();
    }

    // GET /sales — paging, ordering, filtering, isolation.

    [Fact]
    public async Task GetSales_ReturnsOnlyTheCallersOwnSales()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10), 100m);
        Seed(store, UserB, Tomato, new DateOnly(2026, 7, 11), 200m);
        Seed(store, UserB, Carrot, new DateOnly(2026, 7, 12), 300m);

        var result = await SalesQueryHandler(store).Handle(
            new GetSalesQuery { UserId = UserA }, CancellationToken.None);

        result.Data.Items.Should().ContainSingle();
        result.Data.Items[0].PricePerKg.Should().Be(100m);
        result.Data.Total.Should().Be(1, "the total counts the CALLER's rows, not everybody's");
        store.CapturedSalesUserIds.Should().AllBeEquivalentTo(UserA);
    }

    [Fact]
    public async Task GetSales_IsNewestFirst_WithCreatedAtBreakingTiesOnTheSameDay()
    {
        var store = NewStore();
        var day = new DateOnly(2026, 7, 10);

        var older = Seed(store, UserA, Carrot, day, 100m, createdDayOffset: 0);
        var newer = Seed(store, UserA, Carrot, day, 200m, createdDayOffset: 1);
        var laterDay = Seed(store, UserA, Tomato, new DateOnly(2026, 7, 12), 300m, createdDayOffset: 0);

        var result = await SalesQueryHandler(store).Handle(
            new GetSalesQuery { UserId = UserA }, CancellationToken.None);

        result.Data.Items.Select(i => i.Id).Should().Equal(
            new[] { laterDay.Id, newer.Id, older.Id },
            "SaleDate DESC first, then CreatedAtUtc DESC for two sales on the SAME day");
    }

    [Fact]
    public async Task GetSales_FiltersByCrop_ForThePerCropPopupList()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10));
        Seed(store, UserA, Carrot, new DateOnly(2026, 7, 11));
        Seed(store, UserA, Tomato, new DateOnly(2026, 7, 12));

        var result = await SalesQueryHandler(store).Handle(
            new GetSalesQuery { UserId = UserA, CropId = Carrot }, CancellationToken.None);

        result.Data.Items.Should().HaveCount(2);
        result.Data.Items.Should().OnlyContain(i => i.CropId == Carrot);
        result.Data.Total.Should().Be(2, "the total is counted AFTER the crop filter");
    }

    [Fact]
    public async Task GetSales_AnUnknownCropFilter_IsAnEmptyPage_NotAnError()
    {
        var store = NewStore();
        Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10));

        var result = await SalesQueryHandler(store).Handle(
            new GetSalesQuery { UserId = UserA, CropId = UnknownCrop }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue(
            "a read that 400s on a stale id would break the popup the moment a crop went away");
        result.Data.Items.Should().BeEmpty();
        result.Data.Total.Should().Be(0);
    }

    [Fact]
    public async Task GetSales_AnotherFarmersCropFilter_StillReturnsNothingOfTheirs()
    {
        var store = NewStore();
        Seed(store, UserB, Tomato, new DateOnly(2026, 7, 10));

        var result = await SalesQueryHandler(store).Handle(
            new GetSalesQuery { UserId = UserA, CropId = Tomato }, CancellationToken.None);

        result.Data.Items.Should().BeEmpty("the crop filter narrows an already owner-scoped set");
        result.Data.Total.Should().Be(0);
    }

    [Fact]
    public async Task GetSales_EmptyLog_IsAnEmptyPage_NotA404()
    {
        var store = NewStore();

        var result = await SalesQueryHandler(store).Handle(
            new GetSalesQuery { UserId = UserA }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Data.Items.Should().BeEmpty();
        result.Data.Total.Should().Be(0);
        result.Data.Page.Should().Be(1);
        result.Data.PageSize.Should().Be(GetSalesQuery.DefaultPageSize);
    }

    [Theory]
    // (requested page, requested size) -> (served page, served size)
    [InlineData(0, 20, 1, 20)]
    [InlineData(-5, 20, 1, 20)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, -3, 1, 1)]
    [InlineData(1, 51, 1, 50)]
    [InlineData(1, 1000, 1, 50)]
    [InlineData(3, 50, 3, 50)]
    public async Task GetSales_ClampsThePagingParameters_AndEchoesWhatItUsed(
        int page, int pageSize, int servedPage, int servedSize)
    {
        // Clamped, not refused — a farmer scrolling on a phone must never be shown an error because a stale
        // client asked for page 0. (The admin logs pages 400 instead; different audience, different answer.)
        var store = NewStore();

        var result = await SalesQueryHandler(store).Handle(
            new GetSalesQuery { UserId = UserA, Page = page, PageSize = pageSize }, CancellationToken.None);

        result.Data.Page.Should().Be(servedPage);
        result.Data.PageSize.Should().Be(servedSize,
            "the CLAMPED size is echoed: a client that asked for 1000 and got 50 rows must be able to tell");

        GetSalesQuery.MaxPageSize.Should().Be(50, "the cap is the wire contract");
    }

    [Fact]
    public async Task GetSales_PagesWithoutRepeatingOrSkippingARow()
    {
        var store = NewStore();
        for (var i = 0; i < 5; i++)
            Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10).AddDays(i), 100m + i);

        var handler = SalesQueryHandler(store);

        var first = await handler.Handle(
            new GetSalesQuery { UserId = UserA, Page = 1, PageSize = 2 }, CancellationToken.None);
        var second = await handler.Handle(
            new GetSalesQuery { UserId = UserA, Page = 2, PageSize = 2 }, CancellationToken.None);
        var third = await handler.Handle(
            new GetSalesQuery { UserId = UserA, Page = 3, PageSize = 2 }, CancellationToken.None);

        first.Data.Items.Should().HaveCount(2);
        second.Data.Items.Should().HaveCount(2);
        third.Data.Items.Should().HaveCount(1);
        first.Data.Total.Should().Be(5, "the total is the whole matching set, not the page");

        first.Data.Items.Concat(second.Data.Items).Concat(third.Data.Items)
            .Select(i => i.Id).Distinct().Should().HaveCount(5);
    }

    // PUT /sales/{id}.

    [Fact]
    public async Task Update_ReplacesTheMutableFields()
    {
        var store = NewStore();
        var sale = Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10), 100m, Dambulla, 5m, "old note");
        var day = PastDay(2);

        var result = await UpdateHandler(store).Handle(
            new UpdateSaleCommand
            {
                UserId = UserA,
                SaleId = sale.Id,
                MarketId = Keppetipola,
                SaleDate = day,
                PricePerKg = 210.50m,
                QuantityKg = 12m,
                Note = "new note"
            },
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Data.MarketId.Should().Be(Keppetipola);
        result.Data.SaleDate.Should().Be(day);
        result.Data.PricePerKg.Should().Be(210.50m);
        result.Data.QuantityKg.Should().Be(12m);
        result.Data.Note.Should().Be("new note");
        store.Sales.CommitCount.Should().Be(1);
    }

    [Fact]
    public async Task Update_OmittedOptionalKeysCLEARThem()
    {
        // PUT-null semantics: the body is the row's complete new state, so leaving a key out takes the
        // value away. (Contrast PUT /watchlist/{cropId}, where omission means "leave it alone" — there,
        // three independent fields each needed that third state.)
        var store = NewStore();
        var sale = Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10), 100m, Dambulla, 5m, "old note");

        var result = await UpdateHandler(store).Handle(
            new UpdateSaleCommand
            {
                UserId = UserA,
                SaleId = sale.Id,
                SaleDate = PastDay(),
                PricePerKg = 100m
                // marketId, quantityKg and note all omitted.
            },
            CancellationToken.None);

        result.Data.MarketId.Should().BeNull();
        result.Data.MarketName.Should().BeNull("the market's display fields go with it");
        result.Data.MarketShortCode.Should().BeNull();
        result.Data.QuantityKg.Should().BeNull();
        result.Data.Note.Should().BeNull();
    }

    [Fact]
    public async Task Update_CannotChangeTheCrop_BecauseTheCommandHasNoCropField()
    {
        // THE ENFORCEMENT IS THE SHAPE. A wrong-crop sale is deleted and re-added; re-pointing one would
        // silently re-attribute a reported price to a crop the farmer never named.
        typeof(UpdateSaleCommand).GetProperties().Select(p => p.Name)
            .Should().NotContain("CropId");

        var store = NewStore();
        var sale = Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10), 100m);

        var result = await UpdateHandler(store).Handle(
            new UpdateSaleCommand
            {
                UserId = UserA, SaleId = sale.Id, SaleDate = PastDay(), PricePerKg = 120m
            },
            CancellationToken.None);

        result.Data.CropId.Should().Be(Carrot, "the crop is immutable");
        store.Sales.Rows.Single().CropId.Should().Be(Carrot);
    }

    [Fact]
    public async Task Update_AnotherFarmersSale_Is404_AndChangesNothing()
    {
        var store = NewStore();
        var theirs = Seed(store, UserB, Tomato, new DateOnly(2026, 7, 10), 100m, note: "their note");

        var result = await UpdateHandler(store).Handle(
            new UpdateSaleCommand
            {
                UserId = UserA, SaleId = theirs.Id, SaleDate = PastDay(), PricePerKg = 999m
            },
            CancellationToken.None);

        result.Error.Should().Be("sale_not_found",
            "identical to the answer an unknown id gets — a 403 would confirm the row is somebody's");
        store.Sales.Rows.Single().PricePerKg.Should().Be(100m, "their row is untouched");
        store.Sales.Rows.Single().Note.Should().Be("their note");
        store.Sales.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Update_AnUnknownId_IsTheSame404_AsAForeignOne()
    {
        var store = NewStore();

        var result = await UpdateHandler(store).Handle(
            new UpdateSaleCommand
            {
                UserId = UserA, SaleId = Guid.NewGuid(), SaleDate = PastDay(), PricePerKg = 155m
            },
            CancellationToken.None);

        result.Error.Should().Be("sale_not_found");
    }

    [Fact]
    public async Task Update_OwnershipIsAnsweredBeforeThePayload()
    {
        // A caller probing other people's ids must learn nothing from WHICH error comes back: every id that
        // is not theirs answers 404 whatever they put in the body.
        var store = NewStore();
        var theirs = Seed(store, UserB, Tomato, new DateOnly(2026, 7, 10), 100m);

        var result = await UpdateHandler(store).Handle(
            new UpdateSaleCommand
            {
                UserId = UserA, SaleId = theirs.Id, SaleDate = "rubbish", PricePerKg = -1m
            },
            CancellationToken.None);

        result.Error.Should().Be("sale_not_found",
            "not invalid_price — the payload is never even looked at for a row the caller does not own");
    }

    [Theory]
    [InlineData(null, "2026-07-10", "invalid_price")]
    [InlineData("0", "2026-07-10", "invalid_price")]
    [InlineData("-5", "2026-07-10", "invalid_price")]
    [InlineData("100000.01", "2026-07-10", "price_out_of_range")]
    [InlineData("155", "nonsense", "invalid_sale_date")]
    [InlineData("155", "", "invalid_sale_date")]
    public async Task Update_AppliesTheSamePinnedCodesAsRecord(
        string? price, string saleDate, string expectedCode)
    {
        var store = NewStore();
        var sale = Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10), 100m);

        var result = await UpdateHandler(store).Handle(
            new UpdateSaleCommand
            {
                UserId = UserA, SaleId = sale.Id, SaleDate = saleDate, PricePerKg = Money(price)
            },
            CancellationToken.None);

        result.Error.Should().Be(expectedCode);
        store.Sales.Rows.Single().PricePerKg.Should().Be(100m, "a rejected edit changes nothing");
        store.Sales.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Update_FutureDate_Is_sale_date_future()
    {
        var store = NewStore();
        var sale = Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10), 100m);

        var result = await UpdateHandler(store).Handle(
            new UpdateSaleCommand
            {
                UserId = UserA,
                SaleId = sale.Id,
                SaleDate = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30).ToString("yyyy-MM-dd"),
                PricePerKg = 155m
            },
            CancellationToken.None);

        result.Error.Should().Be("sale_date_future");
    }

    [Fact]
    public async Task Update_NoteLengthIsMeasuredOnTheTrimmedValue()
    {
        var store = NewStore();
        var sale = Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10), 100m);
        var handler = UpdateHandler(store);

        var padded = "   " + new string('x', UserSale.NoteMaxLength) + "   ";

        var accepted = await handler.Handle(
            new UpdateSaleCommand
            {
                UserId = UserA, SaleId = sale.Id, SaleDate = PastDay(), PricePerKg = 100m, Note = padded
            },
            CancellationToken.None);

        accepted.IsSuccess.Should().BeTrue();
        accepted.Data.Note.Should().HaveLength(UserSale.NoteMaxLength);

        var rejected = await handler.Handle(
            new UpdateSaleCommand
            {
                UserId = UserA, SaleId = sale.Id, SaleDate = PastDay(), PricePerKg = 100m,
                Note = new string('x', UserSale.NoteMaxLength + 1)
            },
            CancellationToken.None);

        rejected.Error.Should().Be("note_too_long");
    }

    [Fact]
    public async Task Update_UnknownMarket_Is_unknown_market()
    {
        var store = NewStore();
        var sale = Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10), 100m);

        var result = await UpdateHandler(store).Handle(
            new UpdateSaleCommand
            {
                UserId = UserA, SaleId = sale.Id, SaleDate = PastDay(), PricePerKg = 100m,
                MarketId = UnknownMarket
            },
            CancellationToken.None);

        result.Error.Should().Be("unknown_market");
        store.Sales.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Update_AuditsAfterTheCommit_WithoutTheNote()
    {
        var store = NewStore();
        var sale = Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10), 100m);
        var day = PastDay();

        await UpdateHandler(store).Handle(
            new UpdateSaleCommand
            {
                UserId = UserA, SaleId = sale.Id, SaleDate = day, PricePerKg = 210.50m,
                Note = "the buyer haggled hard"
            },
            CancellationToken.None);

        var audited = store.Audited.Should().ContainSingle().Subject;
        audited.Type.Should().Be(UserActivityEventType.SaleUpdated);
        audited.Actor.Should().Be(UserA);
        audited.Details.Should().Be($"VEG000001 · Rs 210.50/kg · {day}");
        audited.Details.Should().NotContain("haggled");
        audited.CommitCountAtCall.Should().Be(1, "AFTER the commit");
    }

    // DELETE /sales/{id}.

    [Fact]
    public async Task Delete_RemovesTheRow()
    {
        var store = NewStore();
        var sale = Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10), 100m);

        var result = await DeleteHandler(store).Handle(
            new DeleteSaleCommand { UserId = UserA, SaleId = sale.Id }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Data.SaleId.Should().Be(sale.Id);
        result.Data.Removed.Should().BeTrue();
        store.Sales.Rows.Should().BeEmpty();
        store.Sales.CommitCount.Should().Be(1);
    }

    [Fact]
    public async Task Delete_ASecondTime_Is404()
    {
        // NOT idempotent, on purpose: telling "you already deleted this" apart from "that was never yours"
        // would reveal which ids exist, and privacy wins over tidiness.
        var store = NewStore();
        var sale = Seed(store, UserA, Carrot, new DateOnly(2026, 7, 10), 100m);
        var handler = DeleteHandler(store);

        (await handler.Handle(
            new DeleteSaleCommand { UserId = UserA, SaleId = sale.Id }, CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        var second = await handler.Handle(
            new DeleteSaleCommand { UserId = UserA, SaleId = sale.Id }, CancellationToken.None);

        second.Error.Should().Be("sale_not_found");
        store.Sales.CommitCount.Should().Be(1, "the second attempt committed nothing");
    }

    [Fact]
    public async Task Delete_AnotherFarmersSale_Is404_AndLeavesItAlone()
    {
        var store = NewStore();
        var theirs = Seed(store, UserB, Tomato, new DateOnly(2026, 7, 10), 100m);

        var result = await DeleteHandler(store).Handle(
            new DeleteSaleCommand { UserId = UserA, SaleId = theirs.Id }, CancellationToken.None);

        result.Error.Should().Be("sale_not_found");
        store.Sales.Rows.Should().ContainSingle("their row is still there");
        store.Sales.CommitCount.Should().Be(0);
    }

    [Fact]
    public async Task Delete_AuditsAfterTheCommit_WithTheDetailsReadBeforeTheRowWent()
    {
        var store = NewStore();
        var day = new DateOnly(2026, 7, 10);
        var sale = Seed(store, UserA, Carrot, day, 155m, note: "regret this one");

        await DeleteHandler(store).Handle(
            new DeleteSaleCommand { UserId = UserA, SaleId = sale.Id }, CancellationToken.None);

        var audited = store.Audited.Should().ContainSingle().Subject;
        audited.Type.Should().Be(UserActivityEventType.SaleDeleted);
        audited.Actor.Should().Be(UserA);
        audited.Details.Should().Be("VEG000001 · Rs 155.00/kg · 2026-07-10",
            "the crop code is read while the row still exists — after the commit there is nothing to join");
        audited.Details.Should().NotContain("regret");
        audited.CommitCountAtCall.Should().Be(1, "AFTER the commit");
    }

    // The wire codes themselves.

    [Fact]
    public void EveryPinnedSalesCode_IsTheExactLiteralTheUiSwitchesOn()
    {
        // Literal strings, deliberately: comparing a constant to itself would still pass after a rename,
        // and these values are a contract with a client we do not compile against.
        PortfolioErrors.SaleNotFound.Should().Be("sale_not_found");
        PortfolioErrors.InvalidPrice.Should().Be("invalid_price");
        PortfolioErrors.PriceOutOfRange.Should().Be("price_out_of_range");
        PortfolioErrors.InvalidSaleDate.Should().Be("invalid_sale_date");
        PortfolioErrors.SaleDateFuture.Should().Be("sale_date_future");
        PortfolioErrors.InvalidQuantity.Should().Be("invalid_quantity");
        PortfolioErrors.NoteTooLong.Should().Be("note_too_long");
        PortfolioErrors.UnknownCrop.Should().Be("unknown_crop");
        PortfolioErrors.UnknownMarket.Should().Be("unknown_market");
    }

    [Fact]
    public void TheSalesCodes_AreClassifiedAsTheRightHttpFamily()
    {
        PortfolioErrors.IsNotFound("sale_not_found").Should().BeTrue("a missing or foreign row is a 404");

        foreach (var code in new[]
                 {
                     "invalid_price", "price_out_of_range", "invalid_sale_date", "sale_date_future",
                     "invalid_quantity", "note_too_long", "unknown_crop", "unknown_market"
                 })
        {
            PortfolioErrors.IsBadRequestCode(code).Should().BeTrue(
                $"{code} is a malformed payload the UI must react to specifically");
            PortfolioErrors.IsUnprocessable(code).Should().BeFalse(
                $"{code} is not a limit the product refuses, it is a value that is wrong");
        }
    }

    [Fact]
    public void TheWatchlistCodesAreUntouchedByTheSalesAdditions()
    {
        // The sales codes joined the existing families rather than replacing them.
        PortfolioErrors.IsNotFound("watchlist_entry_not_found").Should().BeTrue();
        PortfolioErrors.IsUnprocessable("watchlist_full").Should().BeTrue();
        PortfolioErrors.IsUnprocessable("too_many_markets").Should().BeTrue();
        PortfolioErrors.IsUnprocessable("invalid_planted_date").Should().BeTrue();
        PortfolioErrors.IsBadRequestCode("clear_reason_required").Should().BeTrue();
    }
}
