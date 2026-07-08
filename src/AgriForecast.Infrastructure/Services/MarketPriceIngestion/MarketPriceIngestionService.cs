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

    public MarketPriceIngestionService(IDambullaApiClient client, IConfiguration config, ILogger<MarketPriceIngestionService> logger, IUnitofWorkRepository unitofWorkRepository, IMarketPriceRepository marketPriceRepository, ICropRepository cropRepository, IGenericRepository<CropAgronomyProfile> agronomyProfileRepository, IGenericRepository<Market> marketRepository, CodeSettings codeSettings)
    {
        _client = client;
        _config = config;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _marketPriceRepository = marketPriceRepository;
        _cropRepository = cropRepository;
        _agronomyProfileRepository = agronomyProfileRepository;
        _marketRepository = marketRepository;
        _codeSettings = codeSettings;
    }

    public async Task IngestAsync(CancellationToken ct)
    {
        var maxProductId = int.Parse(_config["MarketPriceSources:DambullaDec:MaxProductId"] ?? "101");

        // Fail-closed: without the Dambulla Markets row we cannot link inserts, and inserting
        // unlinked rows would silently recreate the NULL-EconomicCenterId gap this guards against.
        var dambullaMarket = await _marketRepository.GetOneAsyncInclude(m => m.MarketCode == DambullaMarketCode)
            ?? throw new InvalidOperationException(
                $"Market '{DambullaMarketCode}' (Dambulla DEC) not found; aborting ingestion rather than inserting unlinked price rows.");

        // 1. Build ExternalProductId -> CropId lookup from crops already mapped to this source.
        var crops = await _cropRepository.GetAllAsync();
        var cropByProduct = crops
            .Where(c => c.ExternalProductId.HasValue
                        && (string.IsNullOrEmpty(c.Source) || c.Source == SourceName))
            .GroupBy(c => c.ExternalProductId!.Value)
            .ToDictionary(g => g.Key, g => g.First().Id);

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
        var crop = Crop.CreateFromExternalSource(cropName, externalProductId, SourceName, cropCode);
        crop.CropCategoryId = categoryId;

        await _cropRepository.Addasync(crop);

        // Stage the PENDING (unverified, all-fields-NULL) agronomy profile alongside the crop, in
        // the same commit scope as the caller's CommitAsync. Step-5 curation fills and verifies it.
        await _agronomyProfileRepository.AddAsync(CropAgronomyProfile.CreatePending(crop.Id));
        cropsWithProfile.Add(crop.Id);

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
