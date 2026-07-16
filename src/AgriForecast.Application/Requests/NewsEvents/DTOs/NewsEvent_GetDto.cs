using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.NewsEvents.DTOs;

// Read shape for the admin News page. Matches the FE NewsEvent interface (ForecastUI
// src/api/types.ts): { id, eventType, direction, title, description, publishedAt, sourceUrl,
// affectedCropIds, createdAtUtc } — camelCase, enums as integers. affectedMarketIds is an ADDITIVE
// extra field (the FE ignores unknown JSON keys) so the storage side is ready for a future market
// picker; the FE flips from fixtures to live with a client-side swap.
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
