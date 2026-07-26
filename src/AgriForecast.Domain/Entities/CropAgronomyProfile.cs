namespace AgriForecast.Domain.Entities;

// Agronomy metadata for a Crop (1:1, unique CropId). Never seeded with HasData — Crop GUIDs differ per
// database. Rows come from the value-copy migration or are created pending at crop registration.
//
// Planting-season encoding: all four month columns NULL with IsPerennial=false means year-round or
// unknown; Yala months set means a Yala crop; Maha months set means a Maha crop.
public class CropAgronomyProfile
{
    public Guid Id { get; set; }

    // 1:1 owner FK to Crop. Unique — one agronomy profile per crop.
    public Guid CropId { get; set; }

    // Days from planting to first harvest. Drives the forecast horizon and the ML training label
    // (price.shift(-GrowthPeriodDays)), so changing a value here changes the model.
    public int? GrowthPeriodDays { get; set; }

    // How many days the crop keeps yielding once it matures (harvest spread). Null until curated.
    public int? HarvestWindowDays { get; set; }

    // Yala-season planting window (month-of-year 1..12). Null when unknown / not a Yala crop.
    public byte? YalaPlantingStartMonth { get; set; }
    public byte? YalaPlantingEndMonth { get; set; }

    // Maha-season planting window (month-of-year 1..12). Null when unknown / not a Maha crop.
    public byte? MahaPlantingStartMonth { get; set; }
    public byte? MahaPlantingEndMonth { get; set; }

    // True for perennial crops (fruit trees etc.).
    public bool IsPerennial { get; set; }

    // Provenance citation for the agronomy values (e.g. 'legacy-crops-table', a DOA source URL).
    public string? DataSource { get; set; }

    // Date the profile was verified against an authoritative source (date-only). Null until verified.
    public DateTime? VerifiedOn { get; set; }

    // False until an authoritative source confirms the values; new profiles are always unverified.
    public bool IsVerified { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // DataSource marker for profiles auto-created at crop registration, kept distinct from the
    // value-copy migration's 'legacy-crops-table' marker.
    public const string PendingRegistrationSource = "pending-registration";

    // Creates the unverified profile that must accompany every newly-registered Crop.
    public static CropAgronomyProfile CreatePending(Guid cropId)
    {
        return new CropAgronomyProfile
        {
            Id = Guid.NewGuid(),
            CropId = cropId,
            IsPerennial = false,
            IsVerified = false,
            DataSource = PendingRegistrationSource,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
    }
}
