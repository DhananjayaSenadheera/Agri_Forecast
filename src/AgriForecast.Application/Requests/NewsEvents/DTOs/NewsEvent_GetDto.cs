using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.NewsEvents.DTOs;

// Read shape for the admin News page; matches the FE NewsEvent interface, camelCase with enums as
// integers. affectedMarketIds is an additive extra the FE currently ignores.
public class NewsEvent_GetDto
{
    public Guid Id { get; set; }
    public NewsEventType EventType { get; set; }
    public PolicyDirection Direction { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateTime PublishedAt { get; set; }
    public string? SourceUrl { get; set; }
    public List<Guid> AffectedCropIds { get; set; } = new();
    public List<Guid> AffectedMarketIds { get; set; } = new();
    public DateTime CreatedAtUtc { get; set; }
}
