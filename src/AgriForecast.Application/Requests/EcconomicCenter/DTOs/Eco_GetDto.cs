namespace AgriForecast.Application.Requests.EcconomicCenter.DTOs;

public class Eco_GetDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } 
    public string Location { get; set; }
    public string Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}