namespace AgriForecast.Application.Requests.FestivalCalendar.DTOs;

// Full-object update: the create shape plus the Id. The admin console sends the whole entry back.
public class FestivalCalendar_UpdateDto
{
    public Guid Id { get; set; }

    public string FestivalKey { get; set; }

    // Date-only on the wire; the boundary keeps it date-only in storage too.
    public DateTime Date { get; set; }

    // LeadUpDays = 0 is valid (paired-day convention). Validator allows >= 0, not > 0.
    public int LeadUpDays { get; set; }

    public bool IsProvisional { get; set; }

    // Required on mutation (a citation for every change to as-of-joined training data).
    public string? Source { get; set; }
}
