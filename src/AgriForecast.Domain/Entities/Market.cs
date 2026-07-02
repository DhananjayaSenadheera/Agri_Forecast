using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// A market promoted to a first-class dimension in the multi-market model.
// Covers physical DEC trading hubs (Dambulla, Keppetipola, Thambuttegama) as well
// as HARTI / CBSL pseudo-markets that only publish price bulletins. PriceObservation
// rows hang off Markets; EconomicCenter is retained and back-linked (nullable) for
// back-compat. MarketCode uses the MKT###### scheme (see DefaultSetting.Mkt_*).
public class Market
{
    public Guid Id { get; private set; }
    public string MarketCode { get; set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? District { get; set; }
    public MarketType MarketType { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Factory for manual/CRUD-created markets. MarketCode is assigned by the create
    // handler after construction (mirrors EconomicCenter.CreateNew / Crop.CreateForManualEntry).
    public static Market CreateNew(string name, string? district, MarketType marketType)
    {
        return new Market
        {
            Id = Guid.NewGuid(),
            Name = name,
            District = district,
            MarketType = marketType,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    // Mutate-in-place update used by an update handler on a tracked entity.
    public void ApplyUpdate(string name, string? district, MarketType marketType, bool isActive)
    {
        Name = name;
        District = district;
        MarketType = marketType;
        IsActive = isActive;
        UpdatedAt = DateTime.UtcNow;
    }
}
