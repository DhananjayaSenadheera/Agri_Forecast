namespace AgriForecast.Domain.Entities;

// Reference dimension grouping crops (Vegetable / Fruit and sub-categories), mirroring the HARTI
// bulletin grouping. Seeded with HasData using fixed GUIDs and a fixed CreatedAt — never UtcNow, which
// would churn every migration diff. ParentId is a nullable self-FK (null = top level); Code is the
// unique business key.
public class CropCategory
{
    public Guid Id { get; set; }

    // Short, stable business code (e.g. VEG, FRT, VEG-UP, VEG-LOW). Unique.
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    // Nullable self-FK: null => top-level category; set => sub-category of that parent.
    public Guid? ParentId { get; set; }

    public DateTime CreatedAt { get; set; }

    // Fixed seed GUIDs, shared by both crop-registration paths and the CropCode re-code migration.
    public static readonly Guid VegetableId = Guid.Parse("d4c40001-0000-0000-0000-000000000001");
    public static readonly Guid FruitId = Guid.Parse("d4c40001-0000-0000-0000-000000000002");
    public static readonly Guid UpCountryVegetableId = Guid.Parse("d4c40001-0000-0000-0000-000000000003");
    public static readonly Guid LowCountryVegetableId = Guid.Parse("d4c40001-0000-0000-0000-000000000004");

    // Top-level CropCode prefixes.
    public const string VegetablePrefix = "VEG";
    public const string FruitPrefix = "FRT";

    // Maps a category (or sub-category) GUID to its top-level CropCode prefix; unknown GUIDs fall back
    // to VEG. Mirrors the re-code migration's parent rollup.
    public static string PrefixForCategory(Guid categoryId)
    {
        return categoryId == FruitId ? FruitPrefix : VegetablePrefix;
    }
}
