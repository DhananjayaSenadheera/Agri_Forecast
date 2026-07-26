using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.NewsEvents.DTOs;

// Full-object update: the create shape plus the Id, minus PublishedAt.
// PublishedAt is deliberately absent — it is the immutable vintage date, and leaving it out of the wire
// contract means it cannot be rewritten at all. The handler preserves the stored value.
public class NewsEvent_UpdateDto
{
    public Guid Id { get; set; }

    public NewsEventType EventType { get; set; }
    public PolicyDirection Direction { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    // NOTE: no PublishedAt — immutable by construction (see class remarks).

    public string? SourceUrl { get; set; }

    public List<Guid>? AffectedCropIds { get; set; }
    public List<Guid>? AffectedMarketIds { get; set; }
}
