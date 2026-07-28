using AgriForecast.Domain.Constants;

namespace AgriForecast.Domain.Entities;

// One crop a farmer has added to their personal watchlist ("my crops"). Owner-scoped in every direction:
// nothing here is shared, aggregated across users, or read by the ML layer.
//
// This row is the AGGREGATE ROOT for that crop's watched markets: up to
// WatchlistLimits.MaxMarketsPerCrop UserCropWatchMarket children, added and removed only through the
// methods below so the cap cannot be bypassed by a caller mutating a collection. Markets are per crop
// (a farmer sells different crops in different places) and are a DISPLAY choice only — the prediction the
// dashboard shows stays national, so no market here ever reaches the ML layer.
//
// PlantedDate is the farmer's real planting day for this crop, per crop and NOT per market. Null means
// "not told us", which is a legitimate state, not missing data: the dashboard simply shows no
// days-to-harvest for that crop.
public class UserCropWatchlist
{
    private readonly List<UserCropWatchMarket> _markets = new();

    public Guid Id { get; private set; }

    // FK -> Users (Cascade): deleting an account takes its watchlist with it. There is nothing here worth
    // keeping once the owner is gone, and an orphan row would be un-scoped personal data.
    public Guid UserId { get; private set; }

    // FK -> Crops (Restrict): a crop someone is watching cannot be deleted out from under them.
    public Guid CropId { get; private set; }

    // The farmer's own planting day for this crop. Null = not recorded.
    public DateOnly? PlantedDate { get; private set; }

    // Record-keeping only; never a feature.
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>
    /// The markets this crop is watched at, oldest-chosen first — the stable display order. Exposed
    /// read-only: every mutation goes through <see cref="ReplaceMarkets"/> or <see cref="AddMarkets"/>,
    /// which are the only places the cap and the no-duplicates rule are enforced.
    /// </summary>
    public IReadOnlyList<UserCropWatchMarket> Markets =>
        _markets.OrderBy(m => m.CreatedAtUtc).ThenBy(m => m.MarketId).ToList();

    private UserCropWatchlist() { }

    /// <summary>
    /// Adds a crop to a user's watchlist. Times are passed in rather than read from the clock so tests are
    /// deterministic; in production the DB default fills CreatedAtUtc if it is ever omitted.
    /// </summary>
    public static UserCropWatchlist Create(
        Guid userId,
        Guid cropId,
        DateOnly? plantedDate,
        DateTime createdAtUtc)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));

        if (cropId == Guid.Empty)
            throw new ArgumentException("CropId is required.", nameof(cropId));

        if (createdAtUtc == default)
            throw new ArgumentException("CreatedAtUtc is required.", nameof(createdAtUtc));

        GuardPlantedDate(plantedDate, nameof(plantedDate));

        return new UserCropWatchlist
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CropId = cropId,
            PlantedDate = plantedDate,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };
    }

    /// <summary>
    /// Records (or clears, with null) the farmer's planting date for this crop. Returns true when the value
    /// actually changed, so a no-op write does not churn UpdatedAtUtc.
    /// </summary>
    /// <remarks>
    /// Only the FLOOR is guarded here. "Not in the future" needs a clock, and a domain entity that reads
    /// DateTime.UtcNow is untestable and, worse, silently timezone-dependent — so the application layer
    /// owns that half and answers it with the <c>invalid_planted_date</c> wire code.
    /// </remarks>
    public bool SetPlantedDate(DateOnly? plantedDate, DateTime updatedAtUtc)
    {
        if (updatedAtUtc == default)
            throw new ArgumentException("UpdatedAtUtc is required.", nameof(updatedAtUtc));

        GuardPlantedDate(plantedDate, nameof(plantedDate));

        if (PlantedDate == plantedDate)
            return false;

        PlantedDate = plantedDate;
        UpdatedAtUtc = updatedAtUtc;
        return true;
    }

    /// <summary>
    /// FULL REPLACE of this crop's watched markets (an empty list clears them). Duplicates in the input are
    /// collapsed.
    /// </summary>
    /// <remarks>
    /// THIS DOES NOT REORDER. A market already attached KEEPS its original CreatedAtUtc, and therefore its
    /// position in the oldest-chosen display order; only the newly added markets are appended, in the order
    /// the caller listed them. So replacing [A, B] with [C, B] yields B, C — not C, B. That is deliberate:
    /// re-sending an unchanged market must not delete and re-insert it, which would make an unrelated edit
    /// reshuffle the list and (because the transitional dashboard prices a crop at markets[0]) change which
    /// market the farmer is shown. There is currently NO way to express "move B after C"; if reordering
    /// ever becomes a product requirement it needs an explicit position, not a re-send.
    /// </remarks>
    /// <returns>The child rows to insert and the child rows to delete — the caller persists both.</returns>
    /// <exception cref="ArgumentException">
    /// More than <see cref="WatchlistLimits.MaxMarketsPerCrop"/> distinct markets. This is defence in
    /// depth: the handler checks the cap first and answers <c>too_many_markets</c>, so reaching this throw
    /// means a caller bypassed the handler.
    /// </exception>
    public WatchMarketChanges ReplaceMarkets(IEnumerable<Guid> marketIds, DateTime nowUtc)
    {
        if (nowUtc == default)
            throw new ArgumentException("NowUtc is required.", nameof(nowUtc));

        var desired = Normalize(marketIds);

        if (desired.Count > WatchlistLimits.MaxMarketsPerCrop)
            throw new ArgumentException(
                $"A watched crop may follow at most {WatchlistLimits.MaxMarketsPerCrop} markets.",
                nameof(marketIds));

        var removed = _markets.Where(m => !desired.Contains(m.MarketId)).ToList();
        foreach (var row in removed)
            _markets.Remove(row);

        var added = new List<UserCropWatchMarket>();
        foreach (var marketId in desired.Where(id => _markets.All(m => m.MarketId != id)))
        {
            var row = UserCropWatchMarket.Create(Id, marketId, NextInstant(nowUtc, added.Count));
            _markets.Add(row);
            added.Add(row);
        }

        if (added.Count > 0 || removed.Count > 0)
            UpdatedAtUtc = nowUtc;

        return new WatchMarketChanges(added, removed);
    }

    /// <summary>
    /// INSERT-ONLY reconcile: attaches the markets that are not attached yet and touches nothing else.
    /// </summary>
    /// <remarks>
    /// This is what the concurrent-add recovery path uses. After losing a race the winning row is re-read,
    /// and a full replace there would DELETE markets the winner just inserted — a farmer's second tap
    /// silently undoing their first. Insert-only cannot do that. Markets beyond the cap are skipped rather
    /// than throwing: the caller already has their crop, and failing the whole request over a cap they hit
    /// through a race would be worse than quietly honouring the first three.
    /// </remarks>
    /// <returns>The child rows to insert (possibly empty).</returns>
    public IReadOnlyList<UserCropWatchMarket> AddMarkets(IEnumerable<Guid> marketIds, DateTime nowUtc)
    {
        if (nowUtc == default)
            throw new ArgumentException("NowUtc is required.", nameof(nowUtc));

        var added = new List<UserCropWatchMarket>();

        foreach (var marketId in Normalize(marketIds))
        {
            if (_markets.Count >= WatchlistLimits.MaxMarketsPerCrop)
                break;

            if (_markets.Any(m => m.MarketId == marketId))
                continue;

            var row = UserCropWatchMarket.Create(Id, marketId, NextInstant(nowUtc, added.Count));
            _markets.Add(row);
            added.Add(row);
        }

        if (added.Count > 0)
            UpdatedAtUtc = nowUtc;

        return added;
    }

    /// <summary>
    /// Markets attached in one call are stamped one TICK apart, in the order the caller listed them.
    /// </summary>
    /// <remarks>
    /// The display order is (CreatedAtUtc, MarketId), so without this every market of a single request
    /// would share an instant and fall back to sorting by GUID — which quietly reorders the farmer's own
    /// list and, because the dashboard prices a crop at its FIRST market, silently changes which market
    /// they are shown. A tick is the precision of both datetime2(7) and the DateTime itself, so the order
    /// survives the round trip. CreatedAtUtc remains record-keeping only and is never a feature.
    /// </remarks>
    private static DateTime NextInstant(DateTime nowUtc, int position) => nowUtc.AddTicks(position);

    private static List<Guid> Normalize(IEnumerable<Guid>? marketIds)
    {
        var seen = new List<Guid>();

        foreach (var id in marketIds ?? Enumerable.Empty<Guid>())
        {
            if (id == Guid.Empty)
                throw new ArgumentException(
                    "A market id must be a real market id, never Guid.Empty.", nameof(marketIds));

            // De-dup rather than reject: a client sending the same market twice is asking for one market,
            // not committing an error worth a 4xx. The caps are counted AFTER this collapse.
            if (!seen.Contains(id))
                seen.Add(id);
        }

        return seen;
    }

    private static void GuardPlantedDate(DateOnly? plantedDate, string parameterName)
    {
        if (plantedDate.HasValue && plantedDate.Value < WatchlistLimits.EarliestPlantedDate)
            throw new ArgumentException(
                $"PlantedDate must be on or after {WatchlistLimits.EarliestPlantedDate:yyyy-MM-dd}.",
                parameterName);
    }
}

/// <summary>The child rows a market change produced: what to insert and what to delete.</summary>
public sealed record WatchMarketChanges(
    IReadOnlyList<UserCropWatchMarket> Added,
    IReadOnlyList<UserCropWatchMarket> Removed);
