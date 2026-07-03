namespace AgriForecast.Domain.Entities;

public class EconomicCenter
{
    public Guid Id { get; private set; }
    public string EcoCode { get; set; }
    public string Name { get; private set; } = string.Empty;
    public string Location { get; set; }
    public string Description { get; set; }

    // Back-compat link to the promoted Market dimension. Nullable so existing rows
    // remain valid and no existing code path is forced to populate it; the multi-market
    // migration back-fills it by mapping each EconomicCenter to its Market twin.
    public Guid? MarketId { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Factory for economic centers created via the manual CRUD path (Eco_CreateDto).
    // Encapsulates the private-set Id/Name and the created/updated timestamps that the
    // old CreateMap<Eco_CreateDto, EconomicCenter> profile populated (Location and
    // Description were carried by the same-name copy convention). EcoCode is assigned
    // by the create handler after construction (matches prior behaviour).
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

    // Mutate-in-place update used by the update handler on a tracked entity.
    // Reproduces CreateMap<Eco_UpdateDto, EconomicCenter> (Name/Location/Description
    // overwritten unconditionally). UpdatedAt is set by the mapper after this call.
    public void ApplyUpdate(string name, string location, string description)
    {
        Name = name;
        Location = location;
        Description = description;
    }
}