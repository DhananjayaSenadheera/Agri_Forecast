namespace AgriForecast.Domain.Entities;

public class EconomicCenter
{
    public Guid Id { get; private set; }
    
    public string EcoCode { get; set; }
    public string Name { get; private set; } = string.Empty;
    public string Location { get; set; }
    public string Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    
}