using System.Linq.Expressions;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.ExternalSources.DTOs;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using AgriForecast.Infrastructure.Services.MarketPriceIngestion;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgriForecast.Tests;

/// <summary>
/// R2 Step 2.2: MarketPriceIngestionService must guarantee a crop never exists without a
/// CropAgronomyProfile. These tests cover the two profile paths the service owns:
///   * SELF-HEAL — a pre-existing crop that lacks a profile gets a PENDING one (never fails).
///   * AUTO-PROVISION — a crop created on the fly for a new product also gets a PENDING profile.
/// A crop that already has a profile must NOT get a duplicate (idempotent).
/// </summary>
public class MarketPriceIngestionProfileTests
{
    // ── In-memory fakes (repository-backed service, unlike the HTTP-seam ingestion tests) ──────

    private sealed class FakeCropRepository : ICropRepository
    {
        public readonly List<Crop> Crops = new();
        public Task<Crop> Addasync(Crop crop) { Crops.Add(crop); return Task.FromResult(crop); }
        public Task<Crop> UpdateAsync(Crop crop) => Task.FromResult(crop);
        public Task<Crop> DeleteAsync(Crop crop) => Task.FromResult(crop);
        public Task<Crop?> GetByIdAsync(Guid id) => Task.FromResult(Crops.FirstOrDefault(c => c.Id == id));
        public Task<Crop?> GetByCodeAsync(string code) => Task.FromResult(Crops.FirstOrDefault(c => c.CropCode == code));
        public Task<IEnumerable<Crop>> GetAllAsync() => Task.FromResult<IEnumerable<Crop>>(Crops.ToList());
    }

    private sealed class FakeGenericRepository<T> : IGenericRepository<T> where T : class
    {
        public readonly List<T> Items = new();
        public Task AddAsync(T entity) { Items.Add(entity); return Task.CompletedTask; }
        public Task UpdateAsync(T entity) => Task.CompletedTask;
        public Task DeleteAsync(T entity) { Items.Remove(entity); return Task.CompletedTask; }
        public Task<T> GetoneAsync() => Task.FromResult(Items.FirstOrDefault()!);
        public Task<T> GetByIdAsync(Guid id) => Task.FromResult(Items.FirstOrDefault()!);
        public Task<T> GetByIdAsync(int id) => Task.FromResult(Items.FirstOrDefault()!);
        public Task<T> GetByCodeAsync(string ecoCode) => Task.FromResult(Items.FirstOrDefault()!);
        public Task<T?> GetOneAsyncInclude(Expression<Func<T, bool>> predicate)
            => Task.FromResult(Items.AsQueryable().FirstOrDefault(predicate));
        public Task<IEnumerable<T>> GetAllAsync() => Task.FromResult<IEnumerable<T>>(Items.ToList());
        public Task<object> GetManyAsyncInclude(Func<object, bool> func) => Task.FromResult<object>(Items.ToList());
    }

    private sealed class FakeUnitOfWork : IUnitofWorkRepository
    {
        public int Commits { get; private set; }
        public Task CommitAsync() { Commits++; return Task.CompletedTask; }
        public void Dispose() { }
    }

    // MarketPrice store: returns the distinct products the service self-heals from, captures
    // inserts, and no-ops the rest so the feed loop stays empty unless a test feeds items.
    private sealed class FakeMarketPriceRepository : IMarketPriceRepository
    {
        public List<ExternalProduct> DistinctProducts = new();
        public readonly List<MarketPrice> Inserted = new();
        public Task AddAsync(MarketPrice marketPrice, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddRangeAsync(IEnumerable<MarketPrice> marketPrices, CancellationToken ct = default) { Inserted.AddRange(marketPrices); return Task.CompletedTask; }
        public Task<bool> ExistsAsync(string source, int externalProductId, DateOnly priceDate, CancellationToken ct = default) => Task.FromResult(false);
        public Task<HashSet<DateOnly>> GetExistingDatesAsync(string source, int externalProductId, CancellationToken ct = default) => Task.FromResult(new HashSet<DateOnly>());
        public Task<int> BackfillCropIdAsync(string source, int externalProductId, Guid cropId, CancellationToken ct = default) => Task.FromResult(0);
        public Task<List<MarketPrice>> GetByCropIdAsync(Guid cropId, DateOnly from, CancellationToken ct = default) => Task.FromResult(new List<MarketPrice>());
        public Task<List<MarketPrice>> GetRecentByCropIdAsync(Guid cropId, int count, DateOnly asOf, CancellationToken ct = default) => Task.FromResult(new List<MarketPrice>());
        public Task<List<ExternalProduct>> GetDistinctExternalProductsAsync(string source, CancellationToken ct = default) => Task.FromResult(DistinctProducts);
    }

    private sealed class EmptyDambullaClient : IDambullaApiClient
    {
        public Task<List<DambullaChartItemDto>?> GetProductPriceChartAsync(int productId, CancellationToken ct)
            => Task.FromResult<List<DambullaChartItemDto>?>(new List<DambullaChartItemDto>());
    }

    private sealed class SingleItemDambullaClient : IDambullaApiClient
    {
        public Task<List<DambullaChartItemDto>?> GetProductPriceChartAsync(int productId, CancellationToken ct)
            => Task.FromResult<List<DambullaChartItemDto>?>(new List<DambullaChartItemDto>
            {
                new()
                {
                    Id = 1, ProductId = productId, MinPrice = 100m, MaxPrice = 150m,
                    Date = "2026-07-08", Product = new DambullaProductDto { Name = "Tomato" }
                }
            });
    }

    // Backs CodeSettings so the auto-provision path can stamp category-prefixed CropCodes
    // (VEG######/FRT######) via the same per-prefix DefaultSetting counters the API uses.
    private sealed class FakeDefaultSettingRepository : IDefaultSettingRepository
    {
        private readonly DefaultSetting _setting = new()
        {
            Id = 1,
            Veg_Prefix = "VEG", Veg_Padding = 6, Veg_Code = 71,
            Frt_Prefix = "FRT", Frt_Padding = 6, Frt_Code = 27,
            Mkt_Prefix = "MKT", Mkt_Padding = 8, Mkt_Code = 7,
        };
        public Task<DefaultSetting> GetDefaultSetting() => Task.FromResult(_setting);
        public void UpdateDefaultSetting(DefaultSetting defaultSetting) { }
    }

    // Seeds the Dambulla Markets row the service resolves by MarketCode (mirrors the
    // MKT00000001 HasData seed). Tests that want the fail-closed path clear the repo.
    private static FakeGenericRepository<Market> MarketsWithDambulla()
    {
        var markets = new FakeGenericRepository<Market>();
        var dambulla = Market.CreateNew("Dambulla Dedicated Economic Centre", "Matale",
            AgriForecast.Domain.Enums.MarketType.DEC, isEconomicCenter: true);
        dambulla.AssignCode("MKT00000001");
        markets.Items.Add(dambulla);
        return markets;
    }

    private static (MarketPriceIngestionService svc, FakeCropRepository crops,
                    FakeGenericRepository<CropAgronomyProfile> profiles, FakeMarketPriceRepository prices, FakeUnitOfWork uow) Build(
        IDambullaApiClient? client = null, FakeGenericRepository<Market>? markets = null, string maxProductId = "0")
    {
        var crops = new FakeCropRepository();
        var profiles = new FakeGenericRepository<CropAgronomyProfile>();
        var prices = new FakeMarketPriceRepository();
        var uow = new FakeUnitOfWork();
        var codeSettings = new AgriForecast.Application.common.CodeSettings(new FakeDefaultSettingRepository());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MarketPriceSources:DambullaDec:MaxProductId"] = maxProductId })
            .Build();
        var svc = new MarketPriceIngestionService(
            client ?? new EmptyDambullaClient(), config, NullLogger<MarketPriceIngestionService>.Instance,
            uow, prices, crops, profiles, markets ?? MarketsWithDambulla(), codeSettings);
        return (svc, crops, profiles, prices, uow);
    }

    // ── Self-heal: a crop with no profile gets a PENDING one ─────────────────────────────────

    [Fact]
    public async Task SelfHeals_CropWithoutProfile_ByStagingPendingProfile()
    {
        var (svc, crops, profiles, _, _) = Build();
        var orphan = Crop.CreateFromExternalSource("Legacy Carrot", 42, "DAMBULLA_DEC", "DMB000042");
        crops.Crops.Add(orphan);

        await svc.IngestAsync(CancellationToken.None);

        var healed = profiles.Items.Should().ContainSingle().Which;
        healed.CropId.Should().Be(orphan.Id);
        healed.IsVerified.Should().BeFalse("a self-healed profile must be PENDING, never verified");
        healed.DataSource.Should().Be(CropAgronomyProfile.PendingRegistrationSource);
        healed.IsPerennial.Should().BeFalse();
        healed.GrowthPeriodDays.Should().BeNull();
    }

    // ── Idempotent: a crop that already has a profile is not double-staged ───────────────────

    [Fact]
    public async Task DoesNotDuplicate_ProfileForCropThatAlreadyHasOne()
    {
        var (svc, crops, profiles, _, _) = Build();
        var crop = Crop.CreateFromExternalSource("Tomato", 7, "DAMBULLA_DEC", "DMB000007");
        crops.Crops.Add(crop);
        profiles.Items.Add(CropAgronomyProfile.CreatePending(crop.Id));   // pre-existing profile

        await svc.IngestAsync(CancellationToken.None);

        profiles.Items.Should().ContainSingle("an existing profile must not be duplicated");
    }

    // ── Auto-provision: a crop created for a new product also gets a PENDING profile ─────────

    [Fact]
    public async Task AutoProvisionedCrop_FromExistingProduct_AlsoGetsPendingProfile()
    {
        var (svc, crops, profiles, prices, _) = Build();
        // A product with price history but no crop yet -> service creates the crop AND its profile.
        prices.DistinctProducts.Add(new ExternalProduct(99, "New Brinjal"));

        await svc.IngestAsync(CancellationToken.None);

        crops.Crops.Should().ContainSingle(c => c.ExternalProductId == 99);
        var newCrop = crops.Crops.Single(c => c.ExternalProductId == 99);
        var profile = profiles.Items.Should().ContainSingle().Which;
        profile.CropId.Should().Be(newCrop.Id);
        profile.IsVerified.Should().BeFalse();
        profile.DataSource.Should().Be(CropAgronomyProfile.PendingRegistrationSource);
    }

    // ── EconomicCenterId: every inserted DEC row must link to the Dambulla market ────────────

    [Fact]
    public async Task InsertedRows_CarryDambullaEconomicCenterId()
    {
        var markets = MarketsWithDambulla();
        var (svc, crops, _, prices, _) = Build(new SingleItemDambullaClient(), markets, maxProductId: "1");
        var crop = Crop.CreateFromExternalSource("Tomato", 1, "DAMBULLA_DEC", "VEG000007");
        crops.Crops.Add(crop);

        await svc.IngestAsync(CancellationToken.None);

        var row = prices.Inserted.Should().ContainSingle().Which;
        row.EconomicCenterId.Should().Be(markets.Items.Single().Id,
            "every DAMBULLA_DEC insert must link to the Dambulla Markets row — a NULL here silently recreates the Step 3.3 gap");
    }

    [Fact]
    public async Task MissingDambullaMarket_FailsClosed_InsertsNothing()
    {
        var emptyMarkets = new FakeGenericRepository<Market>();
        var (svc, _, _, prices, _) = Build(new SingleItemDambullaClient(), emptyMarkets, maxProductId: "1");

        var act = () => svc.IngestAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MKT00000001*");
        prices.Inserted.Should().BeEmpty("unlinked rows must never be inserted");
    }
}
