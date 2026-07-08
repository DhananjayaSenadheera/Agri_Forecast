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

    // --- Fixed seed GUIDs (HasData reference constants; migration 20260705001104) ---
    // Single source of truth for both registration paths (manual CQRS + ingestion auto-provision)
    // and the CropCode re-code migration. Sub-categories roll up to their top-level parent.
    public static readonly Guid VegetableId = Guid.Parse("d4c40001-0000-0000-0000-000000000001");
    public static readonly Guid FruitId = Guid.Parse("d4c40001-0000-0000-0000-000000000002");
    public static readonly Guid UpCountryVegetableId = Guid.Parse("d4c40001-0000-0000-0000-000000000003");
    public static readonly Guid LowCountryVegetableId = Guid.Parse("d4c40001-0000-0000-0000-000000000004");

    // Top-level CropCode prefixes.
    public const string VegetablePrefix = "VEG";
    public const string FruitPrefix = "FRT";

    // Maps a (possibly sub-)category GUID to its TOP-LEVEL CropCode prefix. Up/Low-country
    // Vegetable roll up to VEG via their parent; only Fruit yields FRT. Any unknown/future GUID
    // defaults to VEG (the safe default that auto-provision + backfill also use). This mirrors the
    // re-code migration's `COALESCE(parent.Code, category.Code)` rollup so both stay consistent.
    public static string PrefixForCategory(Guid categoryId)
    {
        return categoryId == FruitId ? FruitPrefix : VegetablePrefix;
    }
}
