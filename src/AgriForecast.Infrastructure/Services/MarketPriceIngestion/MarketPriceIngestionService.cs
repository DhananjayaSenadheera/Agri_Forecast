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
    private const string SourceName = "DAMBULLA_DEC";

    public MarketPriceIngestionService(IDambullaApiClient client, IConfiguration config, ILogger<MarketPriceIngestionService> logger, IUnitofWorkRepository unitofWorkRepository, IMarketPriceRepository marketPriceRepository, ICropRepository cropRepository)
    {
        _client = client;
        _config = config;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _marketPriceRepository = marketPriceRepository;
        _cropRepository = cropRepository;
    }

    public async Task IngestAsync(CancellationToken ct)
    {
        var maxProductId = int.Parse(_config["MarketPriceSources:DambullaDec:MaxProductId"] ?? "101");

        // 1. Build ExternalProductId -> CropId lookup from crops already mapped to this source.
        var crops = await _cropRepository.GetAllAsync();
        var cropByProduct = crops
            .Where(c => c.ExternalProductId.HasValue
                        && (string.IsNullOrEmpty(c.Source) || c.Source == SourceName))
            .GroupBy(c => c.ExternalProductId!.Value)
            .ToDictionary(g => g.Key, g => g.First().Id);

        // 2. Self-heal: auto-provision a crop for every product that already has price
        //    rows but no crop yet. Historic data ingested before a crop existed becomes
        //    forecastable without any manual catalog curation.
        int cropsAutoCreated = 0;
        var existingProducts = await _marketPriceRepository.GetDistinctExternalProductsAsync(SourceName, ct);
        foreach (var product in existingProducts)
        {
            if (cropByProduct.ContainsKey(product.ExternalProductId))
                continue;

            var newCropId = await CreateCropForProductAsync(product.ExternalProductId, product.Name, ct);
            cropByProduct[product.ExternalProductId] = newCropId;
            cropsAutoCreated++;
        }
        if (cropsAutoCreated > 0)
        {
            await _unitofWorkRepository.CommitAsync();
            _logger.LogInformation("Auto-created {CropsAutoCreated} crops from existing market products.", cropsAutoCreated);
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
                        cropId = await CreateCropForProductAsync(p.ProductId, p.Product?.Name ?? "", ct);
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

    // Creates and stages (not committed) a crop for an external market product.
    // CropCode uses a deterministic source-derived scheme (e.g. DMB000026) so the
    // background worker never races the API's incrementing crop-code counter.
    private async Task<Guid> CreateCropForProductAsync(int externalProductId, string name, CancellationToken ct)
    {
        var cropName = string.IsNullOrWhiteSpace(name) ? $"Product {externalProductId}" : name.Trim();
        var cropCode = $"DMB{externalProductId.ToString().PadLeft(6, '0')}";
        var crop = Crop.CreateFromExternalSource(cropName, externalProductId, SourceName, cropCode);
        await _cropRepository.Addasync(crop);
        return crop.Id;
    }
}
