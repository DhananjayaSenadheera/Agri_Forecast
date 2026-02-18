namespace AgriForecast.Domain.Entities;

public class Crop
{
    public Guid Id { get; private set; }
    public string CropCode { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
   
    
}