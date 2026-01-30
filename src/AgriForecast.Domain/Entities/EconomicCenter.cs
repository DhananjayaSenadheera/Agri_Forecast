namespace AgriForecast.Domain.Entities;

public class EconomicCenter
{
    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;

    private EconomicCenter() { }

    public EconomicCenter(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Economic center name cannot be empty", nameof(name));

        Name = name.Trim();
    }
}