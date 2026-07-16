namespace AgriForecast.Application.Requests.Crop.DTOs;

public class Crop_UpdateDto
{
    public Guid Id { get; set; }
    public string? Name { get;  set; }
    public string? Source { get; set; }
}