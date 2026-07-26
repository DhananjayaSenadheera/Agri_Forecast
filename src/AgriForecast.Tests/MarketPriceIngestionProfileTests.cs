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
/// MarketPriceIngestionService must guarantee a crop never exists without a CropAgronomyProfile. These
/// tests cover the two profile paths the service owns: SELF-HEAL (a pre-existing crop without a profile
/// gets a pending one) and AUTO-PROVISION (a crop created on the fly for a new product also gets one).
/// A crop that already has a profile must not get a duplicate.
/// </summary>
public class MarketPriceIngestionProfileTests
{
    // In-memory fakes (a repository-backed service, unlike the HTTP-seam ingestion tests).

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
                    FakeGenericRepository<CropAgronomyProfile> profiles, FakeMarketPriceRepository prices,
                    FakeGenericRepository<CommodityAlias> aliases, FakeUnitOfWork uow) Build(
        IDambullaApiClient? client = null, FakeGenericRepository<Market>? markets = null, string maxProductId = "0",
        FakeGenericRepository<CommodityAlias>? aliases = null)
    {
        var crops = new FakeCropRepository();
        var profiles = new FakeGenericRepository<CropAgronomyProfile>();
        var prices = new FakeMarketPriceRepository();
        var aliasRepo = aliases ?? new FakeGenericRepository<CommodityAlias>();
        var uow = new FakeUnitOfWork();
        var codeSettings = new AgriForecast.Application.common.CodeSettings(new FakeDefaultSettingRepository());
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["MarketPriceSources:DambullaDec:MaxProductId"] = maxProductId })
            .Build();
        var svc = new MarketPriceIngestionService(
            client ?? new EmptyDambullaClient(), config, NullLogger<MarketPriceIngestionService>.Instance,
            uow, prices, crops, profiles, markets ?? MarketsWithDambulla(), aliasRepo, codeSettings);
        return (svc, crops, profiles, prices, aliasRepo, uow);
    }

    // Run-tracking stats: IngestAsync returns the SAME counts it already logs, which the Worker attaches to
    // the run row. RowsSkipped folds existing-date and zero-price skips, and RowsFetched stays null because
    // the loop tracks no fetched total.
    [Fact]
    public async Task IngestAsync_ReturnsStats_MappingInsertedSkippedAndDistinctCrops()
    {
        // One feed item (product 1) with no pre-existing crop/alias -> provisions a crop + inserts 1.
        var (svc, _, _, _, _, _) = Build(new SingleItemDambullaClient(), maxProductId: "1",
                                         aliases: new FakeGenericRepository<CommodityAlias>());

        var stats = await svc.IngestAsync(CancellationToken.None);

        stats.RowsInserted.Should().Be(1, "the single feed item lands as one insert");
        stats.RowsSkipped.Should().Be(0, "nothing was skipped this run");
        stats.DistinctCrops.Should().Be(1, "the one provisioned crop is the only distinct crop resolved");
        stats.RowsFetched.Should().BeNull("the loop tracks no fetched total — null is the honest value");
    }

    // Self-heal: a crop with no profile gets a pending one.

    [Fact]
    public async Task SelfHeals_CropWithoutProfile_ByStagingPendingProfile()
    {
        var (svc, crops, profiles, _, _, _) = Build();
        var orphan = Crop.CreateFromExternalSource("Legacy Carrot", "DAMBULLA_DEC", "DMB000042");
        crops.Crops.Add(orphan);

        await svc.IngestAsync(CancellationToken.None);

        var healed = profiles.Items.Should().ContainSingle().Which;
        healed.CropId.Should().Be(orphan.Id);
        healed.IsVerified.Should().BeFalse("a self-healed profile must be PENDING, never verified");
        healed.DataSource.Should().Be(CropAgronomyProfile.PendingRegistrationSource);
        healed.IsPerennial.Should().BeFalse();
        healed.GrowthPeriodDays.Should().BeNull();
    }

    // Idempotent: a crop that already has a profile is not double-staged.

    [Fact]
    public async Task DoesNotDuplicate_ProfileForCropThatAlreadyHasOne()
    {
        var (svc, crops, profiles, _, _, _) = Build();
        var crop = Crop.CreateFromExternalSource("Tomato", "DAMBULLA_DEC", "DMB000007");
        crops.Crops.Add(crop);
        profiles.Items.Add(CropAgronomyProfile.CreatePending(crop.Id));   // pre-existing profile

        await svc.IngestAsync(CancellationToken.None);

        profiles.Items.Should().ContainSingle("an existing profile must not be duplicated");
    }

    // Auto-provision: a crop created for a new product also gets a pending profile.

    [Fact]
    public async Task AutoProvisionedCrop_FromExistingProduct_AlsoGetsPendingProfile()
    {
        var (svc, crops, profiles, prices, _, _) = Build();
        // A product with price history but no crop yet -> service creates the crop AND its profile.
        prices.DistinctProducts.Add(new ExternalProduct(99, "New Brinjal"));

        await svc.IngestAsync(CancellationToken.None);

        var newCrop = crops.Crops.Should().ContainSingle(c => c.Name == "New Brinjal").Which;
        var profile = profiles.Items.Should().ContainSingle().Which;
        profile.CropId.Should().Be(newCrop.Id);
        profile.IsVerified.Should().BeFalse();
        profile.DataSource.Should().Be(CropAgronomyProfile.PendingRegistrationSource);
    }

    // EconomicCenterId: every inserted DEC row must link to the Dambulla market.

    [Fact]
    public async Task InsertedRows_CarryDambullaEconomicCenterId()
    {
        var markets = MarketsWithDambulla();
        var (svc, crops, _, prices, _, _) = Build(new SingleItemDambullaClient(), markets, maxProductId: "1");
        var crop = Crop.CreateFromExternalSource("Tomato", "DAMBULLA_DEC", "VEG000007");
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
        var (svc, _, _, prices, _, _) = Build(new SingleItemDambullaClient(), emptyMarkets, maxProductId: "1");

        var act = () => svc.IngestAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MKT00000001*");
        prices.Inserted.Should().BeEmpty("unlinked rows must never be inserted");
    }

    // CommodityAliases is the single governing resolution route. The legacy Crops.ExternalProductId route
    // was retired, so the active DEC-scoped alias keyed on the stringified feed ProductId is now the only
    // crop-to-product mapping. These tests pin the post-cut-over invariants.

    // GOVERNING ROUTE: an inserted feed row gets its CropId from the ACTIVE DEC alias for that
    // product id — no Crops.ExternalProductId lookup remains.
    [Fact]
    public async Task InsertedRows_AreAssignedByAliasRoute()
    {
        var mappedCrop = Crop.CreateFromExternalSource("Tomato", "DAMBULLA_DEC", "VEG000065");
        var aliases = new FakeGenericRepository<CommodityAlias>();
        // SingleItemDambullaClient emits product id 1 -> resolve via alias '1'.
        aliases.Items.Add(CommodityAlias.CreateNew("1", mappedCrop.Id, "DAMBULLA_DEC"));
        var (svc, crops, _, prices, _, _) = Build(new SingleItemDambullaClient(), maxProductId: "1",
                                                  aliases: aliases);
        crops.Crops.Add(mappedCrop);

        await svc.IngestAsync(CancellationToken.None);

        var row = prices.Inserted.Should().ContainSingle().Which;
        row.CropId.Should().Be(mappedCrop.Id,
            "the CommodityAlias route now governs CropId assignment on inserted rows");
    }

    // PRECEDENCE: when a DEC-scoped alias and a null-Source (global) alias both map the same
    // product id, the DEC-scoped one must win DETERMINISTICALLY — specific over general — even
    // when the global alias enumerates last (which would win under naive last-write-wins).
    [Fact]
    public async Task DecScopedAlias_BeatsGlobalAlias_ForSameProductId()
    {
        var decCrop = Crop.CreateFromExternalSource("Tomato", "DAMBULLA_DEC", "VEG000065");
        var globalCrop = Crop.CreateFromExternalSource("Tomato (global)", "DAMBULLA_DEC", "VEG000066");
        var aliases = new FakeGenericRepository<CommodityAlias>();
        aliases.Items.Add(CommodityAlias.CreateNew("1", decCrop.Id, "DAMBULLA_DEC"));
        aliases.Items.Add(CommodityAlias.CreateNew("1", globalCrop.Id, source: null));
        var (svc, crops, _, prices, _, _) = Build(new SingleItemDambullaClient(), maxProductId: "1",
                                                  aliases: aliases);
        crops.Crops.Add(decCrop);
        crops.Crops.Add(globalCrop);

        await svc.IngestAsync(CancellationToken.None);

        var row = prices.Inserted.Should().ContainSingle().Which;
        row.CropId.Should().Be(decCrop.Id,
            "a DEC-scoped alias must beat a global alias for the same product id regardless of enumeration order");
    }

    // AUTO-PROVISION END-STATE: an unmapped feed product must never drop data — it auto-provisions
    // a crop AND stages an ACTIVE DEC alias (Alias = stringified product id) in the same unit of
    // work, so the next run resolves it instead of creating another duplicate crop. NOTE: this pins
    // the end-state (both artifacts present), not commit atomicity — no mid-commit failure is
    // injected here; atomicity rests on the single CommitAsync in the production path.
    [Fact]
    public async Task UnmappedProduct_AutoProvisionsCropAndActiveAlias()
    {
        // No alias for product 99 -> the self-heal path (via DistinctProducts) must create both.
        var (svc, crops, _, prices, aliasRepo, _) = Build(aliases: new FakeGenericRepository<CommodityAlias>());
        prices.DistinctProducts.Add(new ExternalProduct(99, "New Brinjal"));

        await svc.IngestAsync(CancellationToken.None);

        var newCrop = crops.Crops.Should().ContainSingle(c => c.Name == "New Brinjal").Which;
        aliasRepo.Items.Should().ContainSingle(a =>
            a.Alias == "99" && a.Source == "DAMBULLA_DEC" && a.CropId == newCrop.Id && a.IsActive,
            "auto-provision must stage an ACTIVE DEC alias so the product is resolvable next run");
    }

    // NEW FEED PRODUCT (feed loop, not self-heal): a brand-new product arriving from the price feed
    // auto-provisions crop + active alias and the inserted row is keyed to that crop — data is never
    // dropped for want of a pre-existing mapping.
    [Fact]
    public async Task NewFeedProduct_AutoProvisionsAndInsertsRow()
    {
        // SingleItemDambullaClient emits product 1, no alias present -> feed loop must provision.
        var (svc, crops, _, prices, aliasRepo, _) = Build(new SingleItemDambullaClient(), maxProductId: "1",
                                                          aliases: new FakeGenericRepository<CommodityAlias>());

        await svc.IngestAsync(CancellationToken.None);

        var newCrop = crops.Crops.Should().ContainSingle().Which;
        aliasRepo.Items.Should().ContainSingle(a =>
            a.Alias == "1" && a.Source == "DAMBULLA_DEC" && a.CropId == newCrop.Id && a.IsActive);
        var row = prices.Inserted.Should().ContainSingle().Which;
        row.CropId.Should().Be(newCrop.Id, "the inserted row must key to the just-provisioned crop");
    }

    // IDEMPOTENT RESOLUTION (duplicate-alias protection): when the product's alias already exists,
    // re-ingesting resolves through it and must NOT mint a second crop or a second alias.
    [Fact]
    public async Task ExistingAlias_ResolvesWithoutCreatingDuplicateCropOrAlias()
    {
        var mappedCrop = Crop.CreateFromExternalSource("Tomato", "DAMBULLA_DEC", "VEG000065");
        var aliases = new FakeGenericRepository<CommodityAlias>();
        aliases.Items.Add(CommodityAlias.CreateNew("1", mappedCrop.Id, "DAMBULLA_DEC"));
        var (svc, crops, _, prices, aliasRepo, _) = Build(new SingleItemDambullaClient(), maxProductId: "1",
                                                          aliases: aliases);
        crops.Crops.Add(mappedCrop);

        await svc.IngestAsync(CancellationToken.None);

        crops.Crops.Should().ContainSingle("an already-mapped product must not spawn a duplicate crop");
        aliasRepo.Items.Should().ContainSingle("no second alias may be staged for an already-mapped product");
        prices.Inserted.Should().ContainSingle().Which.CropId.Should().Be(mappedCrop.Id);
    }

    // NON-PARTICIPATING ALIASES: a numeric alias scoped to ANOTHER source (e.g. HARTI) must NOT
    // resolve a DEC feed product — the feed then auto-provisions rather than mis-resolving across
    // sources. (DEC-scoped and null-Source/global aliases participate; other-source ones don't.)
    [Fact]
    public async Task OtherSourceAlias_DoesNotResolve_ProductIsAutoProvisioned()
    {
        var hartiCrop = Crop.CreateFromExternalSource("HARTI Tomato", "HARTI", "VEG000065");
        var aliases = new FakeGenericRepository<CommodityAlias>();
        // Numeric alias '1' but scoped to HARTI, not DAMBULLA_DEC -> must be ignored by the DEC route.
        aliases.Items.Add(CommodityAlias.CreateNew("1", hartiCrop.Id, "HARTI"));
        var (svc, crops, _, prices, aliasRepo, _) = Build(new SingleItemDambullaClient(), maxProductId: "1",
                                                          aliases: aliases);
        crops.Crops.Add(hartiCrop);

        await svc.IngestAsync(CancellationToken.None);

        var row = prices.Inserted.Should().ContainSingle().Which;
        row.CropId.Should().NotBe(hartiCrop.Id, "an alias scoped to another source must never resolve a DEC product");
        aliasRepo.Items.Should().Contain(a => a.Alias == "1" && a.Source == "DAMBULLA_DEC" && a.IsActive,
            "the auto-provisioned DEC product must get its own ACTIVE DEC alias");
    }
}
