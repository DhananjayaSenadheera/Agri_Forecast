namespace AgriForecast.Application.Requests.Crop.DTOs;

public class Crop_GetDto
{
    public Guid Id { get; private set; }
    public string Name { get; set; } = string.Empty;
    public int? ExternalProductId { get; set; }
    public string? Source { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}