namespace AgriForecast.Application.Requests.Crop.DTOs;

public class Crop_GetDto
{
    public Guid Id { get; set; }

    // Display-only, assign-once business code (VEG######/FRT######). Never a join/key.
    public string CropCode { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Source { get; set; }

    // The crop's taxonomy node, carrying the DB CropCategories.Code VERBATIM
    // (VEG / FRT / VEG-UP / VEG-LOW). The FE rolls VEG-UP/VEG-LOW under "Vegetables"
    // itself — do NOT roll up server-side. Null only if the crop has no CropCategoryId
    // (shouldn't happen post-backfill; the FE tolerates null).
    public CropCategory_GetDto? Category { get; set; }

    // CropAgronomyProfile.GrowthPeriodDays, exposed ONLY when the forecaster would use it:
    // IsVerified == true && GrowthPeriodDays > 0. Otherwise null. Mirrors the Python serving
    // gate (load.resolve_forecast_gp) so the UI never shows a growth period the model refuses
    // to use (held-unverified crops + continuous/perennial gp-NULL crops surface as null).
    public int? GrowthDays { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Nested taxonomy projection for Crop_GetDto. Code is the verbatim DB CropCategories.Code
// (business key); Name is the human-facing label. No id/parent exposed — the FE keys on Code.
public class CropCategory_GetDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
