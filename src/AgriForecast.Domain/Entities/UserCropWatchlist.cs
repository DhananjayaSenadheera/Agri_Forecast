namespace AgriForecast.Domain.Entities;

// One crop a farmer has added to their personal watchlist ("my crops"). Owner-scoped in every direction:
// nothing here is shared, aggregated across users, or read by the ML layer.
//
// PreferredMarketId is the farmer's HOME MARKET — the market whose observed prices the portfolio
// dashboard shows. It is stored per row, but the product rule is ONE home market per farmer: every write
// that sets it applies to all of that user's rows in a single transaction, and a newly added crop inherits
// the value the user's existing rows already carry. The column is per-row purely to reserve design space
// (a future per-crop market override needs no migration); the application layer owns the invariant.
//
// Null PreferredMarketId means "no market chosen" and is read as the national / economic-centre default,
// NOT as missing data.
public class UserCropWatchlist
{
    public Guid Id { get; private set; }

    // FK -> Users (Cascade): deleting an account takes its watchlist with it. There is nothing here worth
    // keeping once the owner is gone, and an orphan row would be un-scoped personal data.
    public Guid UserId { get; private set; }

    // FK -> Crops (Restrict): a crop someone is watching cannot be deleted out from under them.
    public Guid CropId { get; private set; }

    // FK -> Markets (Restrict), nullable. Null = national / economic-centre default.
    public Guid? PreferredMarketId { get; private set; }

    // Record-keeping only; never a feature. UpdatedAtUtc also orders the home-market resolution when rows
    // are read back, so the newest write always wins.
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime UpdatedAtUtc { get; private set; }

    private UserCropWatchlist() { }

    /// <summary>
    /// Adds a crop to a user's watchlist. Times are passed in rather than read from the clock so tests are
    /// deterministic; in production the DB default fills CreatedAtUtc if it is ever omitted.
    /// </summary>
    public static UserCropWatchlist Create(
        Guid userId,
        Guid cropId,
        Guid? preferredMarketId,
        DateTime createdAtUtc)
    {
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId is required.", nameof(userId));

        if (cropId == Guid.Empty)
            throw new ArgumentException("CropId is required.", nameof(cropId));

        // An all-zeroes GUID is an unset client variable, not "no market": null is how "no market" is
        // spelled here, and letting Guid.Empty through would fail later as an opaque FK violation.
        if (preferredMarketId == Guid.Empty)
            throw new ArgumentException(
                "PreferredMarketId must be null (no market chosen) or a real market id, never Guid.Empty.",
                nameof(preferredMarketId));

        if (createdAtUtc == default)
            throw new ArgumentException("CreatedAtUtc is required.", nameof(createdAtUtc));

        return new UserCropWatchlist
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CropId = cropId,
            PreferredMarketId = preferredMarketId,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc
        };
    }

    /// <summary>
    /// Repoints this row at a home market (or clears it back to the national default with null).
    /// Returns true when the value actually changed, so a caller applying the user-wide invariant does not
    /// churn UpdatedAtUtc on rows that already agree.
    /// </summary>
    public bool SetPreferredMarket(Guid? preferredMarketId, DateTime updatedAtUtc)
    {
        if (preferredMarketId == Guid.Empty)
            throw new ArgumentException(
                "PreferredMarketId must be null (no market chosen) or a real market id, never Guid.Empty.",
                nameof(preferredMarketId));

        if (updatedAtUtc == default)
            throw new ArgumentException("UpdatedAtUtc is required.", nameof(updatedAtUtc));

        if (PreferredMarketId == preferredMarketId)
            return false;

        PreferredMarketId = preferredMarketId;
        UpdatedAtUtc = updatedAtUtc;
        return true;
    }
}
