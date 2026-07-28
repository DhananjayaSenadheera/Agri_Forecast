using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// A market as a first-class dimension: physical DEC trading hubs as well as HARTI / CBSL pseudo-markets
// that only publish price bulletins. PriceObservation rows hang off Markets.
public class Market
{
    public Guid Id { get; private set; }
    // Assigned exactly once by the create handler via AssignCode. The HasData seed and the backfill set
    // it at the DB level, bypassing this setter.
    public string MarketCode { get; private set; } = string.Empty;
    // Short display code shown next to the market name in the UI (e.g. "DEC", "KEP"). Display-only:
    // never a key, an FK or a join column — everything keys on Id, and MarketCode stays the business key.
    // NOT NULL; empty means "no display code assigned yet" and is excluded from the unique index.
    public string ShortCode { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    // Private-set so the district can only change through CreateNew or ApplyUpdate.
    public string? District { get; private set; }
    public MarketType MarketType { get; set; }
    public bool IsActive { get; set; }
    // A Dedicated Economic Centre is a Markets row with this flag set. NOT NULL, defaults false.
    public bool IsEconomicCenter { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // MarketCode is assigned by the create handler after construction. shortCode is optional: omitting it
    // leaves the display code unassigned (empty) rather than inventing one, since a farmer-recognisable
    // abbreviation cannot be derived reliably from a market name.
    public static Market CreateNew(
        string name, string? district, MarketType marketType, bool isEconomicCenter = false,
        string? shortCode = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Market name is required.", nameof(name));

        return new Market
        {
            Id = Guid.NewGuid(),
            Name = name,
            District = district,
            MarketType = marketType,
            IsActive = true,
            IsEconomicCenter = isEconomicCenter,
            ShortCode = NormalizeShortCode(shortCode),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    // Trimmed and upper-cased so "kep" and " KEP " cannot become two different display codes that both
    // satisfy the unique index. Null/blank normalizes to empty = unassigned.
    public static string NormalizeShortCode(string? shortCode)
        => string.IsNullOrWhiteSpace(shortCode) ? string.Empty : shortCode.Trim().ToUpperInvariant();

    // One-time code assignment: refuses an empty code and refuses to re-stamp an already-coded market.
    public void AssignCode(string marketCode)
    {
        if (string.IsNullOrWhiteSpace(marketCode))
            throw new ArgumentException("MarketCode is required.", nameof(marketCode));
        if (!string.IsNullOrEmpty(MarketCode))
            throw new InvalidOperationException("MarketCode is already assigned and cannot be re-stamped.");
        MarketCode = marketCode;
    }

    // Mutate-in-place update used by an update handler on a tracked entity.
    // shortCode deliberately breaks this method's overwrite-unconditionally convention: null means "leave
    // the display code as it is". An unconditional overwrite would blank a coded market whenever a caller
    // updated some other field, and blanked codes are exactly what the unique index cannot police.
    public void ApplyUpdate(
        string name, string? district, MarketType marketType, bool isActive, bool isEconomicCenter = false,
        string? shortCode = null)
    {
        Name = name;
        District = district;
        MarketType = marketType;
        IsActive = isActive;
        IsEconomicCenter = isEconomicCenter;
        if (shortCode is not null)
            ShortCode = NormalizeShortCode(shortCode);
        UpdatedAt = DateTime.UtcNow;
    }
}
