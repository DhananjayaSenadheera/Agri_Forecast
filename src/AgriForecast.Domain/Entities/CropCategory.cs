namespace AgriForecast.Domain.Entities;

// Reference dimension that groups crops (Vegetable / Fruit and their sub-categories),
// mirroring the HARTI bulletin grouping. Seeded point-in-time via HasData with FIXED
// lowercase GUIDs + a FIXED CreatedAt (never UtcNow — that churns the migrations diff),
// following the PolicyFlag / FestivalCalendar reference-entity precedent.
//
// ParentId is a nullable self-FK: top-level categories (Vegetable, Fruit) have ParentId
// null; sub-categories (Up-country / Low-country Vegetable) point at their parent. The
// self-FK uses Restrict so a parent can never be deleted out from under its children.
//
// Code is the human-facing business key (unique). CreatedAt is record-keeping only —
// never used as a feature.
public class CropCategory
{
    public Guid Id { get; set; }

    // Short, stable business code (e.g. VEG, FRT, VEG-UP, VEG-LOW). Unique.
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    // Nullable self-FK: null => top-level category; set => sub-category of that parent.
    public Guid? ParentId { get; set; }

    public DateTime CreatedAt { get; set; }
}
