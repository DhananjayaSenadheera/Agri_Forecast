using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Requests.Prices;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Queries.GetDashboard;

/// <summary>
/// Builds the farmer's portfolio dashboard: their watched crops, each carrying one price block per market
/// that crop is watched at, plus the newest frozen forecast snapshot for the crop.
/// </summary>
/// <remarks>
/// Four rules shape everything below.
/// <para>
/// 1. EVERY WATCHED CROP APPEARS, AND SO DOES EVERY ONE OF ITS MARKETS. Both legs are fail-soft: a crop
/// with no price anywhere and no prediction still comes back, with each missing leg null and a reason code
/// beside it. Dropping a crop, or quietly omitting a market that has no data, would make the farmer's own
/// list disagree with what they chose.
/// </para>
/// <para>
/// 2. A WATCHED MARKET IS NEVER SUBSTITUTED. Each block shows THAT market's own data or an honest null with
/// <c>no_recent_price</c>. The farmer picked that market because it is where they can actually sell;
/// showing another market's number under its name would be a lie about their options. The economic centre
/// appears for exactly one reason — as the single default block of a crop that has chosen NO markets,
/// flagged <c>isDefaultMarket</c> so the UI can name it as a default rather than as their choice.
/// </para>
/// <para>
/// 3. THE PRICE IS ANCHORED PER (CROP, MARKET), AND UNCONSTRAINED. Whatever the freshest usable observation
/// for that crop at that market is, that is what ships — no staleness cutoff and no wall-clock arithmetic,
/// so the response is identical whenever it is called. A crop's price at one market is independent of its
/// price at another AND of every sibling crop: a market that last quoted it in June still reports June's
/// price while a second market reports today's. The 30-day window below gates ONLY whether a PREVIOUS
/// observation is close enough to compare against, and it is measured from that (crop, market)'s own
/// latest date. The observed date ships with every price so the farmer can judge it for themselves.
/// </para>
/// <para>
/// 4. THE PREDICTION IS READ, NEVER RECOMPUTED, AND IS PER CROP. The snapshot's frozen columns pass through
/// verbatim — a Low-confidence fallback stays Low. It sits on the crop, not on a market block, because the
/// model serves ONE national, Dambulla-anchored price; a copy under each market would imply per-market
/// forecasts that do not exist. No forecasting maths happens in C#.
/// </para>
/// <para>
/// READS ARE BATCHED BY MARKET, never per (crop, market): the crops sharing a market are anchored and
/// fetched together, so the cost is two store calls per DISTINCT market plus the watchlist, the snapshots
/// and one economic-centre lookup. With the 10-crop and 3-market caps that is bounded and small.
/// </para>
/// </remarks>
public class GetPortfolioDashboardQueryHandler
    : IRequestHandler<GetPortfolioDashboardQuery, Result<PortfolioDashboard_GetDto>>
{
    /// <summary>
    /// How far back from A (CROP, MARKET)'S OWN freshest observation the trend comparison may look.
    /// <para>
    /// This NEVER gates the latest price — a crop last quoted at that market a year ago still reports that
    /// price. The trend is "versus the immediately previous observation", so this is only the horizon for
    /// FINDING that previous point: 30 days is comfortably wider than any normal publishing gap (weekends,
    /// holidays, a quiet week at a smaller market) while keeping the read to a few hundred rows. A previous
    /// observation older than this yields a price with a null direction rather than a trend measured
    /// against a stale month-old quote.
    /// </para>
    /// </summary>
    public const int TrendWindowDays = 30;

    private readonly IPortfolioReadStore _store;

    public GetPortfolioDashboardQueryHandler(IPortfolioReadStore store) => _store = store;

    public async Task<Result<PortfolioDashboard_GetDto>> Handle(
        GetPortfolioDashboardQuery request, CancellationToken cancellationToken)
    {
        var watchlist = await _store.GetWatchlistAsync(request.UserId, cancellationToken);

        var dto = new PortfolioDashboard_GetDto();
        if (watchlist.Count == 0)
            return Result<PortfolioDashboard_GetDto>.Success(dto);

        // Resolved only for the crops that chose nothing. A watchlist where every crop named a market never
        // needs it, but the lookup is one cheap read and asking for it up front keeps the block-building
        // below branch-free.
        var economicCentre = await _store.GetEconomicCentreMarketAsync(cancellationToken);

        // Which markets each crop is shown for, in the farmer's own first-added-first order. A crop with no
        // chosen markets gets exactly one default block; if the database somehow has no economic centre it
        // gets none, and its card is honestly market-less rather than fabricated.
        var marketsByCrop = watchlist.ToDictionary(
            w => w.CropId,
            w => w.Markets.Count > 0
                ? w.Markets.Select(m => new CropMarket(m.MarketId, m.Name, m.ShortCode, IsDefault: false)).ToList()
                : economicCentre is null
                    ? new List<CropMarket>()
                    : new List<CropMarket>
                    {
                        new(economicCentre.Id, economicCentre.Name, economicCentre.ShortCode, IsDefault: true)
                    });

        // One pair of store calls per DISTINCT market, covering every crop that watches it.
        var legs = new Dictionary<(Guid CropId, Guid MarketId), PriceLeg>();

        foreach (var group in marketsByCrop
                     .SelectMany(kvp => kvp.Value.Select(m => (CropId: kvp.Key, Market: m)))
                     .GroupBy(x => x.Market.MarketId))
        {
            var cropIds = group.Select(x => x.CropId).Distinct().ToArray();

            foreach (var leg in await BuildPriceLegsAsync(cropIds, group.Key, cancellationToken))
                legs[(leg.Key, group.Key)] = leg.Value;
        }

        var cropIdsAll = watchlist.Select(w => w.CropId).Distinct().ToArray();
        var snapshots = (await _store.GetLatestSnapshotsAsync(cropIdsAll, cancellationToken))
            .ToDictionary(s => s.CropId);

        dto.Items = watchlist
            .Select(w =>
            {
                var item = new PortfolioDashboardItem_GetDto
                {
                    CropId = w.CropId,
                    CropName = w.CropName,
                    CropCode = w.CropCode,
                    PlantedDate = PortfolioTime.Fmt(w.PlantedDate),
                    Markets = marketsByCrop[w.CropId]
                        .Select(m => ToMarketDto(m, legs.GetValueOrDefault((w.CropId, m.MarketId))))
                        .ToList()
                };

                if (snapshots.TryGetValue(w.CropId, out var snapshot))
                    item.Prediction = ToPredictionDto(snapshot);
                else
                    item.PredictionUnavailableReason = PortfolioUnavailableReasons.NoSnapshot;

                return item;
            })
            .ToList();

        return Result<PortfolioDashboard_GetDto>.Success(dto);
    }

    // Latest price + trend per crop at ONE market. Two store calls: a PER-CROP anchor (each crop's own
    // freshest usable date at this market), then a window fetch cut from each crop's own anchor.
    //
    // The two concerns are deliberately separate. The anchor set decides WHICH crops this market can serve
    // at all — a crop with any usable observation here is served here, however old it is. The window
    // decides only whether a PREVIOUS point is eligible for the trend. Crops absent from the result are the
    // ones this market has never usefully quoted, and their block ships a null price with a reason.
    private async Task<Dictionary<Guid, PriceLeg>> BuildPriceLegsAsync(
        IReadOnlyCollection<Guid> cropIds, Guid marketId, CancellationToken ct)
    {
        var legs = new Dictionary<Guid, PriceLeg>();

        var anchors = await _store.GetLatestObservedDatesAsync(cropIds, marketId, ct);
        if (anchors.Count == 0)
            return legs; // this market has no usable observation for any of these crops

        var windows = anchors
            .Select(a => new CropObservationWindow(a.CropId, EarliestComparable(a.LatestDate)))
            .ToArray();

        var rows = await _store.GetObservationsAsync(windows, marketId, ct);

        // A single (crop, market, date) can carry several rows — multiple sources, or a re-published
        // bulletin — so the day's unit prices are averaged, exactly as the market overview does.
        var daily = rows
            .Select(r => new
            {
                r.CropId,
                r.Date,
                Unit = ObservedUnitPrice.From(r.MinPrice, r.MaxPrice, r.WholesalePrice, r.RetailPrice)
            })
            .Where(x => x.Unit.HasValue)
            .GroupBy(x => new { x.CropId, x.Date })
            .Select(g => new
            {
                g.Key.CropId,
                g.Key.Date,
                Price = Math.Round(g.Average(x => x.Unit!.Value), 2)
            })
            .ToList();

        foreach (var cropGroup in daily.GroupBy(d => d.CropId))
        {
            var series = cropGroup.OrderBy(d => d.Date).ToList();
            var latest = series[^1];

            // The immediately previous observation, if it is close enough to THIS (crop, market)'s own
            // latest. Not a fixed lag: the trend is "versus last time this crop was quoted HERE", which is
            // what the farmer actually compares to. The eligibility test is re-stated here rather than left
            // to the store's window so the rule lives where it is read; it measures from the latest PRICED
            // day, which is also the anchor — the store only counts rows that carry a price, so a
            // quote-less day can neither win the anchor nor shift this window.
            var previous = series.Count >= 2 && series[^2].Date >= EarliestComparable(latest.Date)
                ? series[^2]
                : null;

            string? direction = null;
            decimal? changePct = null;

            if (previous is not null && previous.Price > 0m)
            {
                changePct = Math.Round((latest.Price - previous.Price) / previous.Price * 100m, 1);
                direction = latest.Price > previous.Price
                    ? "up"
                    : latest.Price < previous.Price
                        ? "down"
                        : "steady";
            }

            legs[latest.CropId] = new PriceLeg(
                latest.Price,
                latest.Date,
                direction,
                changePct,
                // Reported only when it actually backed the direction, so the two can never disagree.
                direction is null ? null : previous!.Price,
                direction is null ? null : previous!.Date);
        }

        return legs;
    }

    // The oldest date a previous observation may carry and still be comparable with an observation on
    // latest — an inclusive TrendWindowDays-day window ending at latest.
    private static DateOnly EarliestComparable(DateOnly latest)
        => latest.AddDays(-(TrendWindowDays - 1));

    private static PortfolioDashboardMarket_GetDto ToMarketDto(CropMarket market, PriceLeg? leg) => new()
    {
        MarketId = market.MarketId,
        Name = market.Name,
        ShortCode = market.ShortCode,
        IsDefaultMarket = market.IsDefault,
        Price = leg is null ? null : ToPriceDto(leg),
        // The market is listed either way; only the price is missing, and it says so instead of borrowing
        // a number from somewhere else.
        PriceUnavailableReason = leg is null ? PortfolioUnavailableReasons.NoRecentPrice : null
    };

    private static PortfolioPrice_GetDto ToPriceDto(PriceLeg leg) => new()
    {
        Price = leg.Price,
        ObservedDate = PortfolioTime.Fmt(leg.ObservedDate),
        Direction = leg.Direction,
        ChangePct = leg.ChangePct,
        PreviousPrice = leg.PreviousPrice,
        PreviousObservedDate = PortfolioTime.Fmt(leg.PreviousObservedDate)
    };

    private static PortfolioPrediction_GetDto ToPredictionDto(PortfolioSnapshotRow s) => new()
    {
        PredictedPrice = s.PredictedPrice,
        LowerBound = s.LowerBound,
        UpperBound = s.UpperBound,
        Confidence = s.Confidence,
        ActivePredictor = s.ActivePredictor,
        ModelVersion = s.ModelVersion,
        SnapshotDate = PortfolioTime.Fmt(s.SnapshotDate),
        HarvestDate = PortfolioTime.Fmt(s.HarvestDate)
    };

    // One market a crop is shown for, before its price is attached.
    private sealed record CropMarket(Guid MarketId, string Name, string ShortCode, bool IsDefault);

    private sealed record PriceLeg(
        decimal Price,
        DateOnly ObservedDate,
        string? Direction,
        decimal? ChangePct,
        decimal? PreviousPrice,
        DateOnly? PreviousObservedDate);
}
