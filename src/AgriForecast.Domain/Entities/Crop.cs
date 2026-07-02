namespace AgriForecast.Domain.Entities;

public class Crop
{
    public Guid Id { get; private set; }
    public string CropCode { get; set; }
    public string Name { get; set; } = string.Empty;

    // Maps this crop to an external market-price source (e.g. Dambulla product id).
    public int? ExternalProductId { get; set; }
    public string? Source { get; set; }

    // --- Agronomic metadata (drives harvest-time price forecasting) ---

    // Days from planting to first harvest. The keystone field: it maps a farmer's
    // planting date to the harvest date whose price we forecast. Null until curated.
    public int? GrowthPeriodDays { get; set; }

    // Typical Sri Lankan cultivation season: "Yala", "Maha", or "Year-round".
    public string? PlantingSeason { get; set; }

    // How many days the crop keeps yielding once it matures (harvest spread).
    public int? HarvestWindowDays { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Factory for crops created via the manual CRUD path (Crop_CreateDto).
    // Encapsulates the private-set Id and the created/updated timestamps that the
    // old CreateMap<Crop_CreateDto, Crop> profile populated. CropCode is assigned
    // by the create handler after construction (matches prior behaviour).
    public static Crop CreateForManualEntry(string name, int? externalProductId, string? source)
    {
        return new Crop
        {
            Id = Guid.NewGuid(),
            Name = name,
            ExternalProductId = externalProductId,
            Source = source,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    // Factory for crops auto-provisioned from an external market-price source
    // (e.g. the Dambulla ingestion). Keeps Id encapsulated while letting the
    // ingestion layer create a fully-formed, source-mapped crop.
    public static Crop CreateFromExternalSource(string name, int externalProductId, string source, string cropCode)
    {
        return new Crop
        {
            Id = Guid.NewGuid(),
            CropCode = cropCode,
            Name = name,
            ExternalProductId = externalProductId,
            Source = source,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }
}