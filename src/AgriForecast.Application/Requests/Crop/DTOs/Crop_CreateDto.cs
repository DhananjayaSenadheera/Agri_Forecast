namespace AgriForecast.Application.Requests.Crop.DTOs;

public class Crop_CreateDto
{
    public string Name { get;  set; }
    public int? ExternalProductId { get; set; }
    public string? Source { get; set; }
}