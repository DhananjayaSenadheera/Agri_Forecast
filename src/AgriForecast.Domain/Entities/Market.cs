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
    public string Name { get; private set; } = string.Empty;
    // Private-set so the district can only change through CreateNew or ApplyUpdate.
    public string? District { get; private set; }
    public MarketType MarketType { get; set; }
    public bool IsActive { get; set; }
    // A Dedicated Economic Centre is a Markets row with this flag set. NOT NULL, defaults false.
    public bool IsEconomicCenter { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // MarketCode is assigned by the create handler after construction.
    public static Market CreateNew(string name, string? district, MarketType marketType, bool isEconomicCenter = false)
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
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

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
    public void ApplyUpdate(string name, string? district, MarketType marketType, bool isActive, bool isEconomicCenter = false)
    {
        Name = name;
        District = district;
        MarketType = marketType;
        IsActive = isActive;
        IsEconomicCenter = isEconomicCenter;
        UpdatedAt = DateTime.UtcNow;
    }
}
