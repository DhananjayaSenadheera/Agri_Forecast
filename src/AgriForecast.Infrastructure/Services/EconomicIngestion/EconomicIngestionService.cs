using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.EconomicIngestion;

// Captures USD/LKR FX data in two steps each pass:
// 1. Backfill: fetches any missing monthly history for the past BackfillMonths via CDN.
// 2. Live: captures today's rate from open.er-api.com.
public class EconomicIngestionService : IEconomicIngestionService
{
    private const string IndicatorCode = "USD_LKR";
    private const string LiveSourceName = "open.er-api.com";
    private const string HistoricalSourceName = "fawazahmed0/currency-api";
    private const int BackfillMonths = 24;

    private readonly IEconomicDataClient _client;
    private readonly IFxHistoricalClient _historicalClient;
    private readonly ILogger<EconomicIngestionService> _logger;
    private readonly IEconomicIndicatorRepository _economicRepo;
    private readonly IUnitofWorkRepository _unitOfWork;

    public EconomicIngestionService(
        IEconomicDataClient client,
        IFxHistoricalClient historicalClient,
        ILogger<EconomicIngestionService> logger,
        IEconomicIndicatorRepository economicRepo,
        IUnitofWorkRepository unitOfWork)
    {
        _client = client;
        _historicalClient = historicalClient;
        _logger = logger;
        _economicRepo = economicRepo;
        _unitOfWork = unitOfWork;
    }

    public async Task IngestAsync(CancellationToken ct)
    {
        await BackfillHistoricalAsync(ct);
        await IngestTodayAsync(ct);
    }

    private async Task BackfillHistoricalAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var backfillFrom = today.AddMonths(-BackfillMonths);
        var backfillTo = today.AddDays(-1);

        var fromDt = backfillFrom.ToDateTime(TimeOnly.MinValue);
        var toDt = backfillTo.ToDateTime(TimeOnly.MinValue);

        var existing = await _economicRepo.GetRangeAsync(fromDt, toDt, IndicatorCode, ct);
        var existingMonths = existing
            .Select(e => new DateOnly(e.Date.Year, e.Date.Month, 1))
            .ToHashSet();

        var missingMonths = new List<DateOnly>();
        var cursor = new DateOnly(backfillFrom.Year, backfillFrom.Month, 1);
        var lastMonth = new DateOnly(backfillTo.Year, backfillTo.Month, 1);
        while (cursor <= lastMonth)
        {
            if (!existingMonths.Contains(cursor))
                missingMonths.Add(cursor);
            cursor = cursor.AddMonths(1);
        }

        if (missingMonths.Count == 0)
        {
            _logger.LogInformation("FX backfill: history complete for the past {N} months.", BackfillMonths);
            return;
        }

        _logger.LogInformation(
            "FX backfill: fetching {Count} missing month(s) ({From:yyyy-MM} to {To:yyyy-MM}).",
            missingMonths.Count, missingMonths[0], missingMonths[^1]);

        var rates = await _historicalClient.GetRatesForMonthStartsAsync(missingMonths, ct);

        int inserted = 0;
        foreach (var rate in rates)
        {
            var date = rate.Date.ToDateTime(TimeOnly.MinValue);
            if (await _economicRepo.ExistsAsync(date, IndicatorCode, ct))
                continue;

            await _economicRepo.AddAsync(
                new EconomicIndicator(date, IndicatorCode, rate.Rate, HistoricalSourceName, DateTime.UtcNow), ct);
            inserted++;
        }

        if (inserted > 0)
        {
            await _unitOfWork.CommitAsync();
            _logger.LogInformation("FX backfill: inserted {Count} historical USD/LKR rate(s).", inserted);
        }
        else
        {
            _logger.LogWarning("FX backfill: fetched no usable rates for {Count} missing month(s).", missingMonths.Count);
        }
    }

    private async Task IngestTodayAsync(CancellationToken ct)
    {
        var fx = await _client.GetLatestUsdToLkrAsync(ct);
        if (fx is null)
        {
            _logger.LogWarning("Economic ingestion: no FX rate from provider; nothing ingested today.");
            return;
        }

        var date = fx.Date.ToDateTime(TimeOnly.MinValue);
        if (await _economicRepo.ExistsAsync(date, IndicatorCode, ct))
        {
            _logger.LogInformation(
                "Economic ingestion: {Code} for {Date:yyyy-MM-dd} already present. Skipped.",
                IndicatorCode, fx.Date);
            return;
        }

        await _economicRepo.AddAsync(
            new EconomicIndicator(date, IndicatorCode, fx.Rate, LiveSourceName, DateTime.UtcNow), ct);
        await _unitOfWork.CommitAsync();

        _logger.LogInformation(
            "Economic ingestion: inserted {Code}={Value} for {Date:yyyy-MM-dd} (source={Source}).",
            IndicatorCode, fx.Rate, fx.Date, LiveSourceName);
    }
}
