namespace AgriForecast.Domain.Entities;

public class Crop
{
    public Guid Id { get; private set; }
    public string CropCode { get; set; }
    public string Name { get; set; } = string.Empty;

    // The crop -> source product mapping lives in CommodityAliases. Never re-add an external product
    // id column to Crops.
    public string? Source { get; set; }

    // Agronomy (growth period, planting season, harvest window) lives in CropAgronomyProfiles;
    // never add those fields back here.

    // Groups the crop under a CropCategory. Nullable while existing crops are backfilled.
    public Guid? CropCategoryId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Manual CRUD create path. CropCode is stamped by the create handler after construction.
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

    // Auto-provision path for crops discovered by ingestion. The source-product mapping is written as
    // a CommodityAlias, not stored here.
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