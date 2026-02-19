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
    private const string SourceName = "DAMBULLA_DEC";

    public MarketPriceIngestionService(IDambullaApiClient client, IConfiguration config, ILogger<MarketPriceIngestionService> logger, IUnitofWorkRepository unitofWorkRepository, IMarketPriceRepository marketPriceRepository)
    {
        _client = client;
        _config = config;
        _logger = logger;
        _unitofWorkRepository = unitofWorkRepository;
        _marketPriceRepository = marketPriceRepository;
    }

    public async Task IngestAsync(CancellationToken ct)
    {
        var maxProductId = int.Parse(_config["MarketPriceSources:DambullaDec:MaxProductId"] ?? "101");

        int inserted = 0;
        int skipped = 0;
        int failedProducts = 0;

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

                    toInsert.Add(new MarketPrice
                    {
                        Id = Guid.NewGuid(),
                        Source = SourceName,
                        ExternalProductId = p.ProductId,
                        ExternalProductName = p.Product?.Name ?? "",
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
            
            
            
            /*var items = await _client.GetProductPriceChartAsync(productId, ct);
            if (items == null || items.Count == 0)
                continue;

            var toInsert = new List<MarketPrice>();

            foreach (var p in items)
            {
                if (!DateOnly.TryParse(p.Date, out var date))
                    continue;

                var exists = await _marketPriceRepository.ExistsAsync(SourceName, p.ProductId, date, ct);
                if (exists) continue;

                toInsert.Add(new MarketPrice
                {
                    Id = Guid.NewGuid(),
                    Source = SourceName,
                    ExternalProductId = p.ProductId,
                    ExternalProductName = p.Product?.Name ?? "",
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
            }*/
        }

        _logger.LogInformation("Dambulla ingestion completed. Inserted={Inserted}, SkippedExisting={Skipped}", inserted, skipped);
    }
    
    
}
    
