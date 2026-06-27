using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.EconomicIngestion;

// Captures the USD/LKR FX rate forward-daily. open.er-api.com is LATEST-ONLY
// (no historical backfill), so each pass records at most one row for the rate's date.
public class EconomicIngestionService : IEconomicIngestionService
{
    private const string IndicatorCode = "USD_LKR";
    private const string SourceName = "open.er-api.com";

    private readonly IEconomicDataClient _client;
    private readonly ILogger<EconomicIngestionService> _logger;
    private readonly IEconomicIndicatorRepository _economicRepo;
    private readonly IUnitofWorkRepository _unitOfWork;

    public EconomicIngestionService(
        IEconomicDataClient client,
        ILogger<EconomicIngestionService> logger,
        IEconomicIndicatorRepository economicRepo,
        IUnitofWorkRepository unitOfWork)
    {
        _client = client;
        _logger = logger;
        _economicRepo = economicRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task IngestAsync(CancellationToken ct)
    {
        var fx = await _client.GetLatestUsdToLkrAsync(ct);
        if (fx is null)
        {
            _logger.LogWarning("Economic ingestion: no FX rate available from provider; nothing ingested.");
            return;
        }

        var date = fx.Date.ToDateTime(TimeOnly.MinValue);

        // Idempotent: skip if we already captured this (date, indicator).
        if (await _economicRepo.ExistsAsync(date, IndicatorCode, ct))
        {
            _logger.LogInformation(
                "Economic ingestion: {Code} for {Date:yyyy-MM-dd} already present. Skipped.",
                IndicatorCode, fx.Date);
            return;
        }

        var record = new EconomicIndicator(date, IndicatorCode, fx.Rate, SourceName, DateTime.UtcNow);
        await _economicRepo.AddAsync(record, ct);
        await _unitOfWork.CommitAsync();

        _logger.LogInformation(
            "Economic ingestion completed. Inserted {Code}={Value} for {Date:yyyy-MM-dd} (source={Source}).",
            IndicatorCode, fx.Rate, fx.Date, SourceName);
    }
}
