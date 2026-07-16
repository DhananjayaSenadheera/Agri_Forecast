namespace AgriForecast.Domain.Entities;

public class Crop
{
    public Guid Id { get; private set; }
    public string CropCode { get; set; }
    public string Name { get; set; } = string.Empty;

    // R2 Step 8.2: the crop->source product mapping is now OWNED by CommodityAliases
    // (DEC aliases keyed on the stringified feed ProductId, Source='DAMBULLA_DEC'). The old
    // Crops.ExternalProductId column was dropped in migration Step82RetireExternalProductIdAndMergePassion.
    // Never re-add an external product id to Crops — resolution goes through CommodityAliases.
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
    public static Crop CreateForManualEntry(string name, string? source, Guid cropCategoryId)
    {
        return new Crop
        {
            Id = Guid.NewGuid(),
            Name = name,
            Source = source,
            CropCategoryId = cropCategoryId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    // Factory for crops auto-provisioned from an external market-price source
    // (e.g. the Dambulla ingestion). Keeps Id encapsulated while letting the
    // ingestion layer create a fully-formed, source-tagged crop. The source-product
    // mapping is written as a CommodityAlias by the ingestion layer, not stored here.
    public static Crop CreateFromExternalSource(string name, string source, string cropCode)
    {
        return new Crop
        {
            Id = Guid.NewGuid(),
            CropCode = cropCode,
            Name = name,
            Source = source,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }
}