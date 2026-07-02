namespace AgriForecast.Domain.Entities;

// Version-controlled mapping of a source's raw commodity label -> a canonical Crop.
// This table is the single home for alias knowledge: parser logic must NEVER hardcode
// aliases (PRD risk R5 High/High: unit/label mismatch across sources). Instead the
// canonical-mapping layer resolves an incoming label against active CommodityAlias rows
// and back-fills PriceObservation.CropId.
//
// Examples of the label divergence this fixes: HARTI emits "Ladies Fingers" while the DEC
// crop is "Lady's Fingers"; HARTI "Luffa" is the DEC "Ridge Gourd" (Luffa acutangula);
// Sinhala-romanised labels like "Bonchi" (beans), "Kohila", "Thibbatu" arrive from other
// sources. One canonical crop can therefore carry many aliases.
//
// Scoping:
//   Source = NULL  -> the alias applies to ALL sources (a global label mapping).
//   Source = "..." -> the alias is scoped to that one source (e.g. "HARTI"), letting the
//                     same raw label map to different crops in different sources if ever needed.
//
// Case-sensitivity: SQL Server's default collation is case-INSENSITIVE, which is DESIRABLE
// here — a source that emits "beans" one day and "Beans" the next still resolves to the same
// crop. The unique indexes (see AgriForecastDbContext) are likewise case-insensitive, so
// "Beans"/"beans" cannot be inserted as two conflicting mappings. Do not force a
// case-sensitive collation on this table.
//
// Setters are private and the entity is constructed via CreateNew (house style, as
// Crop/Market/PriceObservation). EF materialises through its private-setter support.
public class CommodityAlias
{
    public Guid Id { get; private set; }

    // The raw label exactly as a source emits it (e.g. "Beans", "Bonchi", "Kohila").
    public string Alias { get; private set; } = string.Empty;

    // FK -> Crops. Required: an alias with no canonical target is meaningless. OnDelete
    // Restrict so a Crop can never be deleted out from under a live alias mapping.
    public Guid CropId { get; private set; }

    // NULL = applies to all sources; non-null scopes the alias to one source (e.g. "HARTI").
    public string? Source { get; private set; }

    // Optional language tag for the alias text, e.g. "en", "si-rom" (romanised Sinhala),
    // "ta-rom" (romanised Tamil). Nullable; documentation/audit only.
    public string? Language { get; private set; }

    // Soft-disable a stale mapping without deleting history (the mapping is version-controlled).
    public bool IsActive { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    private CommodityAlias() { }

    // Factory for a new alias mapping. Guards a non-empty alias and a non-empty crop id;
    // Source/Language are optional (NULL Source = global). New aliases are active by default.
    public static CommodityAlias CreateNew(string alias, Guid cropId, string? source = null, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(alias))
            throw new ArgumentException("Alias is required.", nameof(alias));
        if (cropId == Guid.Empty)
            throw new ArgumentException("CropId must be a non-empty Guid.", nameof(cropId));

        return new CommodityAlias
        {
            Id = Guid.NewGuid(),
            Alias = alias,
            CropId = cropId,
            Source = source,
            Language = language,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        };
    }
}
