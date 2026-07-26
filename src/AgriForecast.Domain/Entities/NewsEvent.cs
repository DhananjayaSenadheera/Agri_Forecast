using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// A structured market-relevant news event for the admin News page. It captures what happened, which way
// it is expected to push prices and when it became knowable — not manual weights; the model will learn
// weights in a later task, so nothing here is an ML feature yet.
//
// PublishedAt is the knowledge/vintage date and is immutable after create; the update DTO has no such
// field, so it cannot be rewritten.
public class NewsEvent
{
    public Guid Id { get; set; }

    // Category of the event. Stored as int; mirrors PolicyType values so the FE label mapper works.
    public NewsEventType EventType { get; set; }

    // Coarse expected price impact. Reuses PolicyDirection so the FE shares one label mapper.
    public PolicyDirection Direction { get; set; }

    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }

    // When the event became publicly knowable. Date-only and immutable after create.
    public DateTime PublishedAt { get; set; }

    // Provenance link (optional). Validated as an absolute http(s) URL when present.
    public string? SourceUrl { get; set; }

    // Record-keeping only; never a feature.
    public DateTime CreatedAtUtc { get; set; }

    // Optional links to affected crops/markets. Join rows cascade-delete with the event; the Crop and
    // Market sides are Restrict.
    public ICollection<NewsEventCrop> AffectedCrops { get; set; } = new List<NewsEventCrop>();
    public ICollection<NewsEventMarket> AffectedMarkets { get; set; } = new List<NewsEventMarket>();
}
