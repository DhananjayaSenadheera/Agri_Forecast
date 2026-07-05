namespace AgriForecast.Domain.Entities;

// Owns the agronomic metadata for a Crop (1:1, unique CropId FK). These fields drive
// harvest-time price forecasting and are being MOVED here off the Crops table (the three
// legacy Crops columns — GrowthPeriodDays / PlantingSeason / HarvestWindowDays — are
// dropped in a later subtask; do NOT add agronomy fields back onto Crop).
//
// NEVER seeded via HasData: Crop GUIDs are per-database (auto-provisioned), so a profile's
// CropId is per-database too. Rows are created either by the value-copy migration (for the
// legacy crops) or, going forward, as a PENDING profile (IsVerified=0) at crop registration.
//
// PLANTING-SEASON ENCODING CONVENTION (the four *Month columns replace the old
// PlantingSeason string; 2.3's Python encoder derives the season from them):
//   * All four planting-month columns NULL + IsPerennial=false  => YEAR-ROUND (or unknown).
//   * Yala months populated                                     => Yala-season crop.
//   * Maha months populated                                     => Maha-season crop.
// The legacy Crops.PlantingSeason only ever held 'Year-round' or NULL (no Yala/Maha rows),
// so the value-copy migration leaves ALL month columns NULL for every crop and no season
// signal is lost. Real Yala/Maha planting months are filled by Step 5 (DOA research).
public class CropAgronomyProfile
{
    public Guid Id { get; set; }

    // 1:1 owner FK to Crop. Unique — one agronomy profile per crop.
    public Guid CropId { get; set; }

    // Days from planting to first harvest. The keystone forecasting field: it maps a farmer's
    // planting date to the harvest date whose price we forecast, and defines the ML training
    // label / serving horizon (price.shift(-GrowthPeriodDays)). Null until curated.
    public int? GrowthPeriodDays { get; set; }

    // How many days the crop keeps yielding once it matures (harvest spread). Null until curated.
    public int? HarvestWindowDays { get; set; }

    // Yala-season planting window (month-of-year 1..12). Null when unknown / not a Yala crop.
    public byte? YalaPlantingStartMonth { get; set; }
    public byte? YalaPlantingEndMonth { get; set; }

    // Maha-season planting window (month-of-year 1..12). Null when unknown / not a Maha crop.
    public byte? MahaPlantingStartMonth { get; set; }
    public byte? MahaPlantingEndMonth { get; set; }

    // True for perennial crops (fruit trees etc.). Perennial verification is Step 5's job;
    // the value-copy migration and registration both default this to false.
    public bool IsPerennial { get; set; }

    // Provenance citation for the agronomy values (e.g. 'legacy-crops-table', a DOA source URL).
    public string? DataSource { get; set; }

    // Date the profile was verified against an authoritative agronomy source (date-only).
    // Null until verified.
    public DateTime? VerifiedOn { get; set; }

    // False until an authoritative source confirms the values. Both the value-copy migration
    // and crop registration create UNVERIFIED (pending) profiles; only Step-5 curation flips this.
    public bool IsVerified { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
