using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Forecast.DTOs;
using AgriForecast.Application.Requests.Prices;
using AgriForecast.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetMarketOverview;

// Read-only market snapshot for the farmer-app landing screen. Owns the day clamp, price precedence,
// daily aggregation, movers and sparklines; the DB is behind IMarketOverviewStore for unit-testability.
public class GetMarketOverviewQueryHandler
    : IRequestHandler<GetMarketOverviewQuery, Result<MarketOverview_GetDto>>
{
    private const int MinDays = 7;
    private const int MaxDays = 90;
    private const int SparkDays = 14;   // sparkline: last 14 days up to asOf
    private const int MoverLagDays = 7; // previous point = nearest date <= latest-7d
    private const int MaxLatestCrops = 8;
    private const int MoversPerGroup = 5;

    private readonly IMarketOverviewStore _store;
    private readonly ILogger<GetMarketOverviewQueryHandler> _logger;

    public GetMarketOverviewQueryHandler(
        IMarketOverviewStore store,
        ILogger<GetMarketOverviewQueryHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<Result<MarketOverview_GetDto>> Handle(
        GetMarketOverviewQuery request, CancellationToken cancellationToken)
    {
        var windowDays = Math.Clamp(request.Days, MinDays, MaxDays);

        var asOf = await _store.GetLatestObservationDateAsync(cancellationToken);
        if (asOf is null)
        {
            // Empty data is a valid 200 (empty arrays + null asOf), never a 404/failure.
            _logger.LogInformation("Market overview requested but MarketPrices is empty.");
            return Result<MarketOverview_GetDto>.Success(new MarketOverview_GetDto
            {
                AsOf = null,
                WindowDays = windowDays,
                MarketsWithData = 0,
                CropsWithData = 0
            });
        }

        var asOfDate = asOf.Value;
        // Inclusive window of exactly windowDays calendar days ending at asOf.
        var windowFrom = asOfDate.AddDays(-(windowDays - 1));
        // Sparklines need 14 days even when windowDays is clamped to 7, so fetch the wider window once.
        var fetchFrom = asOfDate.AddDays(-(Math.Max(windowDays, SparkDays) - 1));

        var rows = await _store.GetRowsAsync(fetchFrom, asOfDate, cancellationToken);

        // Counts: distinct markets/crops with at least one raw row in the window.
        var windowRaw = rows.Where(r => r.Date >= windowFrom).ToList();
        var marketsWithData = windowRaw.Select(r => r.MarketId).Distinct().Count();
        var cropsWithData = windowRaw.Select(r => r.CropId).Distinct().Count();

        // A day can carry several rows for the same (crop, market), so average the per-row unit prices and
        // keep DayMin/DayMax for the latest-price card.
        var daily = rows
            .Select(r => new { Row = r, Unit = UnitPrice(r.MinPrice, r.MaxPrice, r.WholesalePrice, r.RetailPrice) })
            .Where(x => x.Unit.HasValue)
            .GroupBy(x => new { x.Row.CropId, x.Row.MarketId, x.Row.Date })
            .Select(g => new DailyPoint(
                g.Key.CropId,
                g.First().Row.CropName,
                g.Key.MarketId,
                g.First().Row.MarketName,
                g.Key.Date,
                Math.Round(g.Average(x => x.Unit!.Value), 2),
                g.Where(x => x.Row.MinPrice > 0m).Select(x => (decimal?)x.Row.MinPrice).DefaultIfEmpty(null).Min(),
                g.Where(x => x.Row.MaxPrice > 0m).Select(x => (decimal?)x.Row.MaxPrice).DefaultIfEmpty(null).Max()))
            .ToList();

        var windowDaily = daily.Where(d => d.Date >= windowFrom).ToList();

        var movers = BuildMovers(windowDaily);
        var latestPrices = BuildLatestPrices(windowDaily, daily, asOfDate);

        return Result<MarketOverview_GetDto>.Success(new MarketOverview_GetDto
        {
            AsOf = Fmt(asOfDate),
            WindowDays = windowDays,
            MarketsWithData = marketsWithData,
            CropsWithData = cropsWithData,
            Movers = movers,
            LatestPrices = latestPrices
        });
    }

    // Price precedence: both Min and Max > 0 -> midpoint; else Wholesale; else Retail; else whichever of
    // Max or Min is > 0; otherwise no valid price and the row is skipped. 0 means absent here.
    // The rule itself now lives in ObservedUnitPrice so the portfolio dashboard shows the same number for
    // the same row; this stays as the local name the code below reads by.
    private static decimal? UnitPrice(decimal min, decimal max, decimal wholesale, decimal retail)
        => ObservedUnitPrice.From(min, max, wholesale, retail);

    private static List<MarketMover_GetDto> BuildMovers(List<DailyPoint> windowDaily)
    {
        var movers = new List<MarketMover_GetDto>();

        foreach (var group in windowDaily.GroupBy(d => new { d.CropId, d.MarketId }))
        {
            var series = group.OrderBy(d => d.Date).ToList();
            var latest = series[^1];

            // Previous point: nearest observation on/before latest-7d, still in window.
            var previous = series
                .Where(p => p.Date <= latest.Date.AddDays(-MoverLagDays))
                .OrderByDescending(p => p.Date)
                .FirstOrDefault();

            if (previous is null || previous.Price == 0m)
                continue; // no valid baseline -> excluded

            var changePct = Math.Round((latest.Price - previous.Price) / previous.Price * 100m, 1);
            if (changePct == 0m)
                continue; // neither a riser nor a faller

            movers.Add(new MarketMover_GetDto
            {
                CropId = latest.CropId,
                CropName = latest.CropName,
                MarketName = latest.MarketName,
                LatestPrice = latest.Price,
                PreviousPrice = previous.Price,
                ChangePct = changePct,
                Direction = changePct > 0m ? "up" : "down"
            });
        }

        var risers = movers.Where(m => m.ChangePct > 0m)
            .OrderByDescending(m => m.ChangePct)
            .Take(MoversPerGroup);
        var fallers = movers.Where(m => m.ChangePct < 0m)
            .OrderBy(m => m.ChangePct) // most negative first == |changePct| desc
            .Take(MoversPerGroup);

        return risers.Concat(fallers).ToList();
    }

    private static List<MarketLatestPrice_GetDto> BuildLatestPrices(
        List<DailyPoint> windowDaily, List<DailyPoint> allDaily, DateOnly asOf)
    {
        var sparkFrom = asOf.AddDays(-(SparkDays - 1)); // 14 inclusive days up to asOf

        return windowDaily
            .GroupBy(d => d.CropId)
            .Select(cropGroup =>
            {
                // Pick the market with the freshest data for this crop.
                var best = cropGroup
                    .GroupBy(d => d.MarketId)
                    .Select(mg => new
                    {
                        LatestPoint = mg.OrderBy(d => d.Date).Last(),
                        LatestDate = mg.Max(d => d.Date)
                    })
                    .OrderByDescending(m => m.LatestDate)
                    .ThenBy(m => m.LatestPoint.MarketName)
                    .First()
                    .LatestPoint;

                var spark = allDaily
                    .Where(d => d.CropId == best.CropId
                                && d.MarketId == best.MarketId
                                && d.Date >= sparkFrom
                                && d.Date <= asOf)
                    .OrderBy(d => d.Date)
                    .Select(d => new MarketSparkPoint_GetDto { Date = Fmt(d.Date), Price = d.Price })
                    .ToList();

                return new MarketLatestPrice_GetDto
                {
                    CropId = best.CropId,
                    CropName = best.CropName,
                    MarketName = best.MarketName,
                    Date = Fmt(best.Date),
                    Price = best.Price,
                    MinPrice = best.DayMin ?? best.Price,
                    MaxPrice = best.DayMax ?? best.Price,
                    Spark = spark
                };
            })
            .OrderByDescending(x => x.Date)      // yyyy-MM-dd sorts chronologically as string
            .ThenBy(x => x.CropName)
            .Take(MaxLatestCrops)
            .ToList();
    }

    private static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private sealed record DailyPoint(
        Guid CropId,
        string CropName,
        Guid MarketId,
        string MarketName,
        DateOnly Date,
        decimal Price,
        decimal? DayMin,
        decimal? DayMax);
}
