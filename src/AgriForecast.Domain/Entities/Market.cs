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
    // MarketCode is the human-facing business key. Private-set: it is assigned exactly
    // once by the create handler via AssignCode (mirrors how Crop.CropCode / EconomicCenter
    // are code-stamped) and never mutated ad hoc thereafter. The HasData seed and the
    // ECOMAP back-fill set it at the DB level, not through this setter, so they are unaffected.
    public string MarketCode { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    // District is private-set: mutated only through the CreateNew factory and ApplyUpdate,
    // keeping this controlled dimension from being poked with unvalidated free text.
    public string? District { get; private set; }
    public MarketType MarketType { get; set; }
    public bool IsActive { get; set; }
    // IsEconomicCenter folds the retiring EconomicCenters dimension into Markets (R2 D-DF3):
    // a Dedicated Economic Centre is now just a Markets row with this flag set. NOT NULL,
    // defaults false so every existing/ingestion-provisioned market is a plain market unless
    // explicitly promoted. Public-set so a future economic-centre registration path can set it.
    public bool IsEconomicCenter { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Factory for manual/CRUD-created markets. MarketCode is assigned by the create
    // handler after construction (mirrors EconomicCenter.CreateNew / Crop.CreateForManualEntry).
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

    // Controlled one-time code assignment: the create handler stamps the generated
    // MKT###### code here after construction (mirrors Crop/EconomicCenter code-stamping).
    // Guards an empty code and refuses to silently re-stamp an already-coded market.
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
