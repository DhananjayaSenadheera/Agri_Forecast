using AgriForecast.Application.common;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.MarketPriceIngestion;

public class MarketPriceIngestionService : IMarketPriceIngestionService
{
    private readonly IDambullaApiClient _client;
    private readonly IConfiguration _config;
    private readonly ILogger<MarketPriceIngestionService> _logger;
    private readonly IUnitofWorkRepository _unitofWorkRepository;
    private readonly IMarketPriceRepository _marketPriceRepository;
    private readonly ICropRepository _cropRepository;
    private readonly IGenericRepository<CropAgronomyProfile> _agronomyProfileRepository;
    private readonly IGenericRepository<Market> _marketRepository;
    private readonly IGenericRepository<CommodityAlias> _commodityAliasRepository;
    private readonly CodeSettings _codeSettings;
    private const string SourceName = "DAMBULLA_DEC";

    // Every DAMBULLA_DEC row is a Dambulla economic-centre price, so inserts must carry the
    // Dambulla Markets link (FK added in R2 Step 3.3). Resolved at runtime by MarketCode —
    // never a hardcoded GUID (same rule as the Step 3.3 backfill migration and HARTI loader).
    private const string DambullaMarketCode = "MKT00000001";

    // Default category for auto-provisioned crops = top-level Vegetable (fixed seed GUID).
    // Fruit-by-keyword refinement bumps obvious fruits to top-level Fruit instead.
    // (Seed GUIDs are the fixed reference-table constants on CropCategory — single source of truth
    // shared with the manual registration path and the CropCode re-code migration.)
    private static readonly Guid VegetableCategoryId = CropCategory.VegetableId;
    private static readonly Guid FruitCategoryId = CropCategory.FruitId;

    // Conservative fruit keyword list. Melon variants are deliberately EXCLUDED: "watermelon"
    // and other melons appear under vegetables in Sri Lankan market groupings, so keeping them
    // as the Vegetable default avoids mis-classifying them as Fruit. Covers the common
    // "avacado" misspelling seen in feed product names.
    private static readonly string[] FruitKeywords =
    {
        "banana", "mango", "papaya", "pineapple", "guava", "avocado", "avacado"
    };

    public MarketPriceIngestionService(IDambullaApiClient client, IConfiguration config, ILogger<MarketPriceIngestionService> logger, IUnitofWorkRepository unitofWorkRepository, IMarketPriceRepository marketPriceRepository, ICropRepository cropRepository, IGenericRepository<CropAgronomyProfile> agronomyProfileRepository, IGenericRepository<Market> marketRepository, IGenericRepository<CommodityAlias> commodityAliasRepository, CodeSettings codeSettings)
    {
        _client = client;
        _config = config;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _marketPriceRepository = marketPriceRepository;
        _cropRepository = cropRepository;
        _agronomyProfileRepository = agronomyProfileRepository;
        _marketRepository = marketRepository;
        _commodityAliasRepository = commodityAliasRepository;
        _codeSettings = codeSettings;
    }

    public async Task<IngestionRunStats> IngestAsync(CancellationToken ct)
    {
        var maxProductId = int.Parse(_config["MarketPriceSources:DambullaDec:MaxProductId"] ?? "101");

        // Fail-closed: without the Dambulla Markets row we cannot link inserts, and inserting
        // unlinked rows would silently recreate the NULL-EconomicCenterId gap this guards against.
        var dambullaMarket = await _marketRepository.GetOneAsyncInclude(m => m.MarketCode == DambullaMarketCode)
            ?? throw new InvalidOperationException(
                $"Market '{DambullaMarketCode}' (Dambulla DEC) not found; aborting ingestion rather than inserting unlinked price rows.");

        // 1. R2 Step 8.2: the CommodityAliases route is the SINGLE GOVERNING resolver. Build the
        //    feed ProductId -> CropId lookup from ACTIVE DEC-scoped (or null-Source) aliases whose
        //    Alias parses as an integer ProductId (Alias='11', Source='DAMBULLA_DEC'). The legacy
        //    Crops.ExternalProductId route (and its column) was retired in this subtask; there is no
        //    longer a shadow comparison — this route IS the route.
        var cropByProduct = BuildAliasRoute(await _commodityAliasRepository.GetAllAsync());

        // Load all crops solely to self-heal agronomy profiles below (a crop must never exist
        // without one). This is NOT a resolution route — resolution is alias-only.
        var crops = await _cropRepository.GetAllAsync();

        // Track which crops already have an agronomy profile so we can (a) self-heal any crop
        // that somehow exists without one and (b) avoid double-staging a profile for a crop we
        // just provisioned in this same pass. Loaded once; kept in sync as we create profiles.
        var cropsWithProfile = (await _agronomyProfileRepository.GetAllAsync())
            .Select(p => p.CropId)
            .ToHashSet();

        // 2. Self-heal: auto-provision a crop for every product that already has price
        //    rows but no crop yet. Historic data ingested before a crop existed becomes
        //    forecastable without any manual catalog curation.
        int cropsAutoCreated = 0;
        var existingProducts = await _marketPriceRepository.GetDistinctExternalProductsAsync(SourceName, ct);
        foreach (var product in existingProducts)
        {
            if (cropByProduct.ContainsKey(product.ExternalProductId))
                continue;

            var newCropId = await CreateCropForProductAsync(product.ExternalProductId, product.Name, cropsWithProfile, ct);
            cropByProduct[product.ExternalProductId] = newCropId;
            cropsAutoCreated++;
        }

        // 2b. Self-heal agronomy profiles: a crop must never exist without a profile going
        //     forward, but legacy/edge crops predating this rule may lack one. Stage a PENDING
        //     profile for any such crop (matches how we self-heal missing crops above). Idempotent:
        //     crops that already have a profile are skipped via cropsWithProfile.
        int profilesHealed = 0;
        foreach (var crop in crops)
        {
            if (cropsWithProfile.Contains(crop.Id))
                continue;

            await _agronomyProfileRepository.AddAsync(CropAgronomyProfile.CreatePending(crop.Id));
            cropsWithProfile.Add(crop.Id);
            profilesHealed++;
        }

        if (cropsAutoCreated > 0 || profilesHealed > 0)
        {
            await _unitofWorkRepository.CommitAsync();
            if (cropsAutoCreated > 0)
                _logger.LogInformation("Auto-created {CropsAutoCreated} crops from existing market products.", cropsAutoCreated);
            if (profilesHealed > 0)
                _logger.LogInformation("Self-healed {ProfilesHealed} missing agronomy profiles (pending).", profilesHealed);
        }

        // 3. Backfill CropId on existing rows now that every product has a crop.
        int backfilled = 0;
        foreach (var (productId, cropId) in cropByProduct)
            backfilled += await _marketPriceRepository.BackfillCropIdAsync(SourceName, productId, cropId, ct);
        if (backfilled > 0)
            _logger.LogInformation("Backfilled CropId on {Backfilled} existing market price rows.", backfilled);

        int inserted = 0;
        int skipped = 0;
        int zeroSkipped = 0;
        int newCropsFromFeed = 0;
        int failedProducts = 0;
        // Distinct crops actually TOUCHED (inserted) this pass — not the whole ~96-crop alias route.
        var insertedCropIds = new HashSet<Guid>();

        // 4. Pull the latest prices, creating crops on the fly for brand-new products.
        for (int productId = 1; productId <= maxProductId; productId++)
        {
            try
            {
                var items = await _client.GetProductPriceChartAsync(productId, ct);
                if (items == null || items.Count == 0)
                    continue;

                var existingDates = await _marketPriceRepository.GetExistingDatesAsync(SourceName, productId, ct);
                var toInsert = new List<MarketPrice>(capacity: items.Count);
                foreach (var p in items)
                {
                    if (!DateOnly.TryParse(p.Date, out var date))
                        continue;

                    if (existingDates.Contains(date))
                    {
                        skipped++;
                        continue;
                    }

                    // Market-closed days carry no price signal; skip rather than store noise.
                    if (p.MinPrice <= 0 && p.MaxPrice <= 0)
                    {
                        zeroSkipped++;
                        continue;
                    }

                    if (!cropByProduct.TryGetValue(p.ProductId, out var cropId))
                    {
                        cropId = await CreateCropForProductAsync(p.ProductId, p.Product?.Name ?? "", cropsWithProfile, ct);
                        cropByProduct[p.ProductId] = cropId;
                        newCropsFromFeed++;
                    }

                    toInsert.Add(new MarketPrice
                    {
                        Id = Guid.NewGuid(),
                        Source = SourceName,
                        ExternalProductId = p.ProductId,
                        ExternalProductName = p.Product?.Name ?? "",
                        CropId = cropId,
                        EconomicCenterId = dambullaMarket.Id,
                        PriceDate = date,
                        MinPrice = p.MinPrice,
                        MaxPrice = p.MaxPrice,
                        RetrievedAtUtc = DateTime.UtcNow
                    });
                }

                if (toInsert.Count > 0)
                {
                    await _marketPriceRepository.AddRangeAsync(toInsert, ct);
                    await _unitofWorkRepository.CommitAsync();
                    inserted += toInsert.Count;
                    // Only crops on rows that actually committed count toward DistinctCrops.
                    // (Every inserted row carries a non-null CropId — it is resolved/provisioned above.)
                    foreach (var mp in toInsert)
                        if (mp.CropId is Guid insertedCropId)
                            insertedCropIds.Add(insertedCropId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to ingest market price for productId={ProductId}", productId);
                failedProducts++;
            }
        }

        _logger.LogInformation(
            "Dambulla ingestion completed. Inserted={Inserted}, SkippedExisting={Skipped}, ZeroSkipped={ZeroSkipped}, CropsAutoCreated={CropsAutoCreated}, NewCropsFromFeed={NewCropsFromFeed}, Backfilled={Backfilled}, FailedProducts={FailedProducts}",
            inserted, skipped, zeroSkipped, cropsAutoCreated, newCropsFromFeed, backfilled, failedProducts);

        // Return the SAME counts already logged (no recount) for the source's IngestionRun row.
        // RowsSkipped folds both dedup-skips (existing dates) and zero-price market-closed skips.
        // DistinctCrops (S3) is the number of distinct crops on rows actually INSERTED this pass —
        // NOT the whole ~96-crop alias route, which would report ~96 every pass regardless of work
        // done. RowsFetched / coverage dates are left null — the loop tracks no fetched total or date
        // window, and null is the honest "not tracked" value rather than a fabricated number.
        return new IngestionRunStats(
            RowsInserted: inserted,
            RowsSkipped: skipped + zeroSkipped,
            DistinctCrops: insertedCropIds.Count);
    }

    // Builds the governing resolution lookup: feed ProductId -> CropId, from ACTIVE DEC-scoped
    // (or null-Source / global — the CommodityAlias contract says NULL applies to all sources)
    // aliases whose Alias parses as an integer ProductId. Non-numeric / inactive / other-source
    // aliases are skipped (they simply don't participate — never a misresolution). Precedence is
    // DETERMINISTIC: a DEC-scoped alias always beats a global one for the same ProductId
    // (specific over general), regardless of enumeration order.
    private static Dictionary<int, Guid> BuildAliasRoute(IEnumerable<CommodityAlias> aliases)
    {
        var route = new Dictionary<int, Guid>();
        var globals = new Dictionary<int, Guid>();
        foreach (var a in aliases)
        {
            if (!a.IsActive || (a.Source != null && a.Source != SourceName))
                continue;
            if (!int.TryParse(a.Alias, out var productId))
                continue;
            if (a.Source == null)
                globals[productId] = a.CropId;
            else
                route[productId] = a.CropId;
        }
        foreach (var (productId, cropId) in globals)
            route.TryAdd(productId, cropId);
        return route;
    }

    // Creates and stages (not committed) a crop for an external market product, plus its PENDING
    // agronomy profile (a crop must never exist without one).
    //
    // R2 D-DF4: CropCode is now the category-prefixed VEG######/FRT###### scheme (was the
    // source-derived DMB###### scheme). The prefix follows the crop's resolved CropCategoryId
    // (fruit-keyword ⇒ FRT, else VEG), consuming the per-prefix DefaultSetting counter via
    // CodeSettings.GetCropCode — the SAME counter the manual CQRS path uses. The counter increment
    // commits in the caller's CommitAsync scope alongside the crop insert. CropCode has no unique
    // index/FK/join (display-only), so a rare worker/API counter race yields at worst a duplicate
    // cosmetic code, never a failed insert — accepted trade-off for a single consistent code scheme.
    // cropsWithProfile is updated so the self-heal pass never double-stages a profile for a crop
    // provisioned in this same run.
    private async Task<Guid> CreateCropForProductAsync(int externalProductId, string name, HashSet<Guid> cropsWithProfile, CancellationToken ct)
    {
        var cropName = string.IsNullOrWhiteSpace(name) ? $"Product {externalProductId}" : name.Trim();

        // Assign a default CropCategory so auto-provisioned crops are never left uncategorised:
        // top-level Vegetable, or top-level Fruit when the product name matches a fruit keyword.
        var categoryId = ResolveCategoryId(cropName);

        // CropCode prefix follows the TOP-LEVEL category of that CropCategoryId.
        var prefix = CropCategory.PrefixForCategory(categoryId);
        var cropCode = await _codeSettings.GetCropCode(prefix)
                       ?? $"{prefix}{externalProductId.ToString().PadLeft(6, '0')}"; // fail-safe fallback
        var crop = Crop.CreateFromExternalSource(cropName, SourceName, cropCode);
        crop.CropCategoryId = categoryId;

        await _cropRepository.Addasync(crop);

        // Stage the PENDING (unverified, all-fields-NULL) agronomy profile alongside the crop, in
        // the same commit scope as the caller's CommitAsync. Step-5 curation fills and verifies it.
        await _agronomyProfileRepository.AddAsync(CropAgronomyProfile.CreatePending(crop.Id));
        cropsWithProfile.Add(crop.Id);

        // R2 Step 8.2: a brand-new feed product is mapped SOLELY by an ACTIVE DEC CommodityAlias
        // (Alias = the stable ProductId stringified, Source='DAMBULLA_DEC'), staged in the same
        // unit of work as the crop + pending profile. This alias is the only crop<->product mapping
        // (Crops.ExternalProductId is gone); without it the product would be unmapped and would
        // auto-provision a fresh duplicate crop on the next run.
        await _commodityAliasRepository.AddAsync(
            CommodityAlias.CreateNew(externalProductId.ToString(), crop.Id, SourceName));

        // Log the assigned category only — never the raw feed payload / request bodies / headers.
        _logger.LogInformation(
            "Auto-provisioned crop {CropCode} (productId={ProductId}) assigned CropCategoryId={CropCategoryId} (pending agronomy profile staged).",
            cropCode, externalProductId, crop.CropCategoryId);

        return crop.Id;
    }

    // Maps an auto-provisioned crop name to a top-level CropCategory. Default is Vegetable;
    // returns Fruit only when the (case-insensitive) name contains a fruit keyword. Melons are
    // intentionally NOT fruit keywords (kept as Vegetable — see FruitKeywords remarks).
    private static Guid ResolveCategoryId(string cropName)
    {
        var lower = cropName.ToLowerInvariant();
        foreach (var keyword in FruitKeywords)
        {
            if (lower.Contains(keyword))
            {
                return FruitCategoryId;
            }
        }
        return VegetableCategoryId;
    }
}
