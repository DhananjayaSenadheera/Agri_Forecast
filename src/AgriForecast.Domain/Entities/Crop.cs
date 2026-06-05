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
   
    
}