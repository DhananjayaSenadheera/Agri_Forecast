namespace AgriForecast.Domain.Entities;

public class Crop
{
    public Guid Id { get; private set; }
    public string CropCode { get; set; }
    public string Name { get; set; } = string.Empty;

    // Maps this crop to an external market-price source (e.g. Dambulla product id).
    public int? ExternalProductId { get; set; }
    public string? Source { get; set; }

    // --- Agronomic metadata ---
    // Agronomy (GrowthPeriodDays, PlantingSeason, HarvestWindowDays) moved to
    // CropAgronomyProfiles (1:1) in R2 Step 2.1 and dropped from Crops in Step 2.4.
    // CropAgronomyProfiles is the sole owner; never add agronomy fields back here.

    // Groups this crop under a CropCategory (Vegetable / Fruit + sub-categories).
    // Nullable: the 96 existing crops are backfilled by a later subtask, not here, so
    // existing rows stay valid. Becomes required after that name-keyed backfill.
    public Guid? CropCategoryId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Factory for crops created via the manual CRUD path (Crop_CreateDto).
    // Encapsulates the private-set Id and the created/updated timestamps that the
    // old CreateMap<Crop_CreateDto, Crop> profile populated. CropCode is assigned
    // by the create handler after construction (matches prior behaviour).
    public static Crop CreateForManualEntry(string name, int? externalProductId, string? source, Guid cropCategoryId)
    {
        return new Crop
        {
            Id = Guid.NewGuid(),
            Name = name,
            ExternalProductId = externalProductId,
            Source = source,
            CropCategoryId = cropCategoryId,
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