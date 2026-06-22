namespace AgriForecast.Domain.Entities;

public class Crop
{
    public Guid Id { get; private set; }
    public string CropCode { get; set; }
    public string Name { get; set; } = string.Empty;

    // Maps this crop to an external market-price source (e.g. Dambulla product id).
    public int? ExternalProductId { get; set; }
    public string? Source { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

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