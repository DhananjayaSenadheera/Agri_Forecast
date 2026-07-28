namespace AgriForecast.Domain.Entities;

// One market a farmer follows FOR ONE WATCHED CROP — a child of UserCropWatchlist, never a standalone
// record. Markets are per crop because a farmer sells different crops at different places; the prediction
// itself stays national, so this table only ever decides which prices are DISPLAYED and compared.
//
// Owner-scoped by inheritance: the row carries no UserId of its own, so there is no way to read it except
// through its parent watchlist row, which is already user-scoped by every query that loads it.
public class UserCropWatchMarket
{
    public Guid Id { get; private set; }

    // FK -> UserCropWatchlist (Cascade): the parent is the aggregate root. Removing the crop from the
    // watchlist takes its markets with it, and deleting an account cascades through both.
    public Guid UserCropWatchlistId { get; private set; }

    // FK -> Markets (Restrict), NOT NULL. There is no "null market" here — a crop with no chosen market
    // simply has no rows, which is a different (and legitimate) state from a row pointing at nothing.
    public Guid MarketId { get; private set; }

    // Record-keeping AND the stable display order: markets are shown oldest-chosen first, so a farmer's
    // list does not reshuffle between requests. Never a feature.
    public DateTime CreatedAtUtc { get; private set; }

    private UserCropWatchMarket() { }

    /// <summary>
    /// Attaches a market to a watched crop. Times are passed in rather than read from the clock so tests
    /// are deterministic; the DB default fills CreatedAtUtc if it is ever omitted.
    /// </summary>
    public static UserCropWatchMarket Create(Guid userCropWatchlistId, Guid marketId, DateTime createdAtUtc)
    {
        if (userCropWatchlistId == Guid.Empty)
            throw new ArgumentException(
                "UserCropWatchlistId is required.", nameof(userCropWatchlistId));

        // An all-zeroes GUID is an unset client variable, not a market. Letting it through would surface
        // later as an opaque FK violation instead of a validation error at the edge.
        if (marketId == Guid.Empty)
            throw new ArgumentException("MarketId is required and must be a real market id.", nameof(marketId));

        if (createdAtUtc == default)
            throw new ArgumentException("CreatedAtUtc is required.", nameof(createdAtUtc));

        return new UserCropWatchMarket
        {
            Id = Guid.NewGuid(),
            UserCropWatchlistId = userCropWatchlistId,
            MarketId = marketId,
            CreatedAtUtc = createdAtUtc
        };
    }
}
