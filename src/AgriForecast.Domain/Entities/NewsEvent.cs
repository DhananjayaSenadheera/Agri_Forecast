using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// A structured market-relevant news event captured for the admin News page (ADM-7).
//
// Owner decision (2026-07-12): capture STRUCTURED, honest facts (what happened + which way it is
// expected to push prices + WHEN it became knowable), NOT manual point weights. The forecasting
// model will LEARN event weights in a later, separate task. This entity is therefore CAPTURE +
// STORAGE ONLY today — nothing here is yet an ML feature, which is why (unlike PolicyFlag /
// FestivalCalendarEntry) there is deliberately NO training-data-warning idiom on its mutations.
//
// PublishedAt is the point-in-time knowledge/as-of/vintage date and is IMMUTABLE after create:
// once a fact is recorded as knowable on a date, rewriting that date would falsify the vintage the
// future feature layer will as-of-join on (same discipline as MacroSeriesPoint.PublishedAt). The
// UpdateDto does not even carry a PublishedAt field, so it cannot be rewritten by construction.
public class NewsEvent
{
    public Guid Id { get; set; }

    // Category of the event. Stored as int; mirrors PolicyType values so the FE label mapper works.
    public NewsEventType EventType { get; set; }

    // Coarse expected price impact (-1 down / 0 neutral / +1 up). Reuses the existing
    // PolicyDirection enum rather than duplicating a -1/0/1 enum — the FE reuses the same
    // PolicyDirection mapper for a news event's direction.
    public PolicyDirection Direction { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    // The knowledge/as-of/vintage date — when the event became publicly knowable. Stored date-only
    // (no hidden time) and IMMUTABLE after create (see class remarks).
    public DateTime PublishedAt { get; set; }

    // Provenance link (optional). Validated as an absolute http(s) URL when present.
    public string? SourceUrl { get; set; }

    // Audit: when the row was recorded (record-keeping only; never a feature).
    public DateTime CreatedAtUtc { get; set; }

    // Optional many-to-many links to the crops / markets this event is relevant to. Join rows are
    // owned by the event (Cascade on NewsEvent delete); the Crop/Market side is Restrict.
    public ICollection<NewsEventCrop> AffectedCrops { get; set; } = new List<NewsEventCrop>();
    public ICollection<NewsEventMarket> AffectedMarkets { get; set; } = new List<NewsEventMarket>();
}
