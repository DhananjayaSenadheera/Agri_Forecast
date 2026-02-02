namespace AgriForecast.Domain.Entities;

public class Crop
{
    public Guid Id { get; private set; }
    
    public string CropCode { get; set; }
    public string Name { get; private set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; }
    
    public DateTime UpdatedAt { get; set; }
    private Crop() { } 
    public Crop(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Crop name cannot be empty", nameof(name));

        Name = name.Trim();
    }
    
}