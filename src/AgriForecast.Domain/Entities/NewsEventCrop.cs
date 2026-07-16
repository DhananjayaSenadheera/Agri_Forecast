namespace AgriForecast.Domain.Entities;

// Join row linking a NewsEvent to a Crop it is relevant to (many-to-many). Composite key
// (NewsEventId, CropId) — a crop cannot be linked to the same event twice.
//
// FK behaviour (explicit, load-bearing — silent Cascade/SetNull FKs are exactly the ones that
// bite): the link is CASCADE-deleted with its parent NewsEvent (the association is meaningless
// without the event). The Crop side is RESTRICT: a Crop that is still referenced by a news event
// cannot be deleted until the link is removed. Restrict (not Cascade/SetNull) matches this
// codebase's uniform dimension-FK posture (CommodityAlias -> Crop, CropAgronomyProfiles -> Crop,
// CropPrice -> Market all Restrict) and fails LOUD rather than silently dropping a curated
// association when someone deletes a crop.
public class NewsEventCrop
{
    public Guid NewsEventId { get; set; }
    public Guid CropId { get; set; }
}
