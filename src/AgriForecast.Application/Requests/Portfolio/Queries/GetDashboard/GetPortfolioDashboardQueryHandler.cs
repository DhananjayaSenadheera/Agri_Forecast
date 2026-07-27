using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Requests.Prices;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Queries.GetDashboard;

/// <summary>
/// Builds the farmer's portfolio dashboard: their watched crops, each decorated with the latest observed
/// price + trend at their home market and the newest frozen forecast snapshot.
/// </summary>
/// <remarks>
/// Three rules shape everything below.
/// <para>
/// 1. EVERY WATCHED CROP APPEARS. Both decorations are fail-soft: a crop with no price and no prediction
/// still comes back, with both legs null and a reason code each. Dropping it would make the farmer's own
/// list silently disagree with what they added.
/// </para>
/// <para>
/// 2. THE LATEST PRICE IS UNCONSTRAINED, AND ANCHORED PER CROP. Whatever the freshest usable observation
/// for a crop at that market is, that is what ships — there is no staleness cutoff and no wall-clock
/// arithmetic, so the response is identical whenever it is called and a crop is never made to look
/// priceless (or pushed to the fallback market) because it exists. The trend window below applies ONLY to
/// whether a PREVIOUS observation is close enough to compare against, and it is measured from each crop's
/// own latest date, never from a sibling crop's. The observed date ships with every price so the farmer
/// can see for themselves how current it is.
/// </para>
/// <para>
/// 3. THE PREDICTION IS READ, NEVER RECOMPUTED. The snapshot's frozen columns pass through verbatim —
/// a Low-confidence fallback stays Low. No forecasting maths happens in C#.
/// </para>
/// </remarks>
public class GetPortfolioDashboardQueryHandler
    : IRequestHandler<GetPortfolioDashboardQuery, Result<PortfolioDashboard_GetDto>>
{
    /// <summary>
    /// How far back from A CROP'S OWN freshest observation the trend comparison may look.
    /// <para>
    /// This NEVER gates the latest price — a crop last quoted a year ago still reports that price. The
    /// trend is "versus the immediately previous observation", so this is only the horizon for FINDING that
    /// previous point: 30 days is comfortably wider than any normal publishing gap (weekends, holidays, a
    /// quiet week at a smaller market) while keeping the read to a few hundred rows per crop. A crop whose
    /// previous observation is older than this reports its price with a null direction rather than a trend
    /// measured against a stale month-old quote.
    /// </para>
    /// </summary>
    public const int TrendWindowDays = 30;

    private readonly IPortfolioReadStore _store;

    public GetPortfolioDashboardQueryHandler(IPortfolioReadStore store) => _store = store;

    public async Task<Result<PortfolioDashboard_GetDto>> Handle(
        GetPortfolioDashboardQuery request, CancellationToken cancellationToken)
    {
        var watchlist = await _store.GetWatchlistAsync(request.UserId, cancellationToken);

        // The economic centre is both the default home market and the price fallback, so it is resolved
        // even for an empty watchlist: the empty-state screen still names the market it would show.
        var economicCentre = await _store.GetEconomicCentreMarketAsync(cancellationToken);

        var chosenMarketId = HomeMarket.Resolve(
            watchlist.Select(w => new HomeMarketCandidate(w.CropId, w.PreferredMarketId, w.UpdatedAtUtc)));

        // A chosen market that no longer resolves cannot happen (the FK is Restrict), but falling back to
        // the economic centre is the safe answer if it ever did.
        var chosenMarket = chosenMarketId.HasValue
            ? await _store.GetMarketAsync(chosenMarketId.Value, cancellationToken)
            : null;

        var homeMarket = chosenMarket ?? economicCentre;
        var usingDefault = chosenMarket is null;

        var dto = new PortfolioDashboard_GetDto
        {
            HomeMarket = homeMarket is null
                ? null
                : new PortfolioHomeMarket_GetDto
                {
                    MarketId = homeMarket.Id,
                    Name = homeMarket.Name,
                    IsEconomicCenter = homeMarket.IsEconomicCenter,
                    IsDefault = usingDefault
                }
        };

        if (watchlist.Count == 0)
            return Result<PortfolioDashboard_GetDto>.Success(dto);

        var cropIds = watchlist.Select(w => w.CropId).Distinct().ToArray();

        // Home-market prices first.
        var pricesByCrop = homeMarket is null
            ? new Dictionary<Guid, PriceLeg>()
            : await BuildPriceLegsAsync(cropIds, homeMarket, isFallback: false, cancellationToken);

        // Then, only for the crops the home market could not serve, the economic-centre fallback. Skipped
        // entirely when the home market IS the economic centre — there is nothing further to fall back to.
        if (economicCentre is not null && homeMarket is not null && economicCentre.Id != homeMarket.Id)
        {
            var missing = cropIds.Where(id => !pricesByCrop.ContainsKey(id)).ToArray();
            if (missing.Length > 0)
            {
                var fallbackLegs = await BuildPriceLegsAsync(
                    missing, economicCentre, isFallback: true, cancellationToken);

                foreach (var kvp in fallbackLegs)
                    pricesByCrop[kvp.Key] = kvp.Value;
            }
        }

        var snapshots = (await _store.GetLatestSnapshotsAsync(cropIds, cancellationToken))
            .ToDictionary(s => s.CropId);

        dto.Items = watchlist
            .Select(w =>
            {
                var item = new PortfolioDashboardItem_GetDto
                {
                    CropId = w.CropId,
                    CropName = w.CropName,
                    CropCode = w.CropCode
                };

                if (pricesByCrop.TryGetValue(w.CropId, out var leg))
                    item.Price = ToPriceDto(leg);
                else
                    item.PriceUnavailableReason = PortfolioUnavailableReasons.NoRecentPrice;

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
    // at all — a crop with any usable observation here is served here, however old it is, so a stale crop
    // is never handed to the economic-centre fallback and never labelled isFallbackMarket for a price its
    // own market really does have. The window decides only whether a PREVIOUS point is eligible for the
    // trend.
    private async Task<Dictionary<Guid, PriceLeg>> BuildPriceLegsAsync(
        IReadOnlyCollection<Guid> cropIds,
        PortfolioMarketRow market,
        bool isFallback,
        CancellationToken ct)
    {
        var legs = new Dictionary<Guid, PriceLeg>();

        var anchors = await _store.GetLatestObservedDatesAsync(cropIds, market.Id, ct);
        if (anchors.Count == 0)
            return legs; // this market has no usable observation for any of these crops

        var windows = anchors
            .Select(a => new CropObservationWindow(a.CropId, EarliestComparable(a.LatestDate)))
            .ToArray();

        var rows = await _store.GetObservationsAsync(windows, market.Id, ct);

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

            // The immediately previous observation, if it is close enough to THIS crop's own latest. Not a
            // fixed lag: the trend is "versus last time this crop was quoted here", which is what the
            // farmer actually compares to. The eligibility test is re-stated here rather than left to the
            // store's window so the rule lives where it is read — and so it stays measured against the
            // latest USABLE row, which can be older than the anchor if the anchor day carried no price.
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
                market.Id,
                market.Name,
                isFallback,
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

    private static PortfolioPrice_GetDto ToPriceDto(PriceLeg leg) => new()
    {
        Price = leg.Price,
        ObservedDate = PortfolioTime.Fmt(leg.ObservedDate),
        MarketId = leg.MarketId,
        MarketName = leg.MarketName,
        IsFallbackMarket = leg.IsFallbackMarket,
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

    private sealed record PriceLeg(
        decimal Price,
        DateOnly ObservedDate,
        Guid MarketId,
        string MarketName,
        bool IsFallbackMarket,
        string? Direction,
        decimal? ChangePct,
        decimal? PreviousPrice,
        DateOnly? PreviousObservedDate);
}
