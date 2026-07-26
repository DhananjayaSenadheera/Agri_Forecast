namespace AgriForecast.Domain.Entities;

// Join row linking a NewsEvent to a Crop. Composite key (NewsEventId, CropId).
// Cascade-deleted with the parent event; the Crop side is Restrict, so deleting a crop that a news
// event still references fails loudly instead of silently dropping the association.
public class NewsEventCrop
{
    public Guid NewsEventId { get; set; }
    public Guid CropId { get; set; }
}
