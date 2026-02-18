namespace AgriForecast.Application.Requests.EcconomicCenter.DTOs;

public class Eco_UpdateDto
{
    public Guid Id { get; set; }
    public string? Name { get; set; }
    public string? Location { get; set; }
    public string? Description { get; set; }
}