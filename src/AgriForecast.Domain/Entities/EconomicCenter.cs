namespace AgriForecast.Domain.Entities;

public class EconomicCenter
{
    public Guid Id { get; private set; }
    public string EcoCode { get; set; }
    public string Name { get; private set; } = string.Empty;
    public string Location { get; set; }
    public string Description { get; set; }

    // Link to the Market that replaced this dimension. Nullable; backfilled by the multi-market migration.
    public Guid? MarketId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Manual CRUD create path. EcoCode is stamped by the create handler after construction.
    public static EconomicCenter CreateNew(string name, string location, string description)
    {
        return new EconomicCenter
        {
            Id = Guid.NewGuid(),
            Name = name,
            Location = location,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }

    // In-place update used by the update handler; UpdatedAt is set by the mapper afterwards.
    public void ApplyUpdate(string name, string location, string description)
    {
        Name = name;
        Location = location;
        Description = description;
    }
}