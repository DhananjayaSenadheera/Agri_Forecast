using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.NewsEvents.DTOs;

// Create shape for the admin News page (ADM-7). Enums serialize as INTEGERS on the wire (no
// JsonStringEnumConverter) — EventType 0..8 (mirrors PolicyType), Direction -1/0/1.
public class NewsEvent_CreateDto
{
    // Event category. Int on the wire; validator enforces a defined-enum value.
    public NewsEventType EventType { get; set; }

    // Coarse expected price impact (-1 down / 0 neutral / +1 up). Reuses PolicyDirection.
    public PolicyDirection Direction { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    // The knowledge/as-of/vintage date. REQUIRED on create; date-only in storage. IMMUTABLE
    // afterwards — the UpdateDto deliberately omits this field so it can never be rewritten.
    public DateTime PublishedAt { get; set; }

    // Optional provenance link; validated as an absolute http(s) URL when present.
    public string? SourceUrl { get; set; }

    // Optional many-to-many links. Each id must resolve to an existing Crop/Market (validator).
    public List<Guid>? AffectedCropIds { get; set; }
    public List<Guid>? AffectedMarketIds { get; set; }
}
