namespace AgriForecast.Domain.Entities;

public class EconomicCenter
{
    public Guid Id { get; private set; }
    
    public string EcoCode { get; set; }
    public string Name { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    private EconomicCenter() { }
    public EconomicCenter(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Economic center name cannot be empty", nameof(name));

        Name = name.Trim();
    }
}