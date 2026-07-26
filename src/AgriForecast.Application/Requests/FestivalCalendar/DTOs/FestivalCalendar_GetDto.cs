namespace AgriForecast.Application.Requests.FestivalCalendar.DTOs;

// Read shape for the admin Festivals page; matches the FE FestivalEntry interface.
public class FestivalCalendar_GetDto
{
    public Guid Id { get; set; }
    public string FestivalKey { get; set; }
    public DateTime Date { get; set; }
    public int LeadUpDays { get; set; }
    public bool IsProvisional { get; set; }
    public string? Source { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
