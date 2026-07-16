using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.NewsEvents.DTOs;

// Full-object update: mirrors NewsEvent_CreateDto plus the Id, MINUS PublishedAt.
//
// PublishedAt is DELIBERATELY ABSENT. It is the knowledge/as-of/vintage date and is immutable
// after create — omission beats validation here: the field simply cannot be rewritten because the
// wire contract does not carry it (same vintage discipline as MacroSeriesPoint.PublishedAt). The
// update handler preserves the stored PublishedAt untouched.
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
