namespace AgriForecast.Application.Requests.Crop.DTOs;

public class Crop_GetDto
{
    public Guid Id { get; set; }

    // Display-only, assign-once business code (VEG######/FRT######). Never a join/key.
    public string CropCode { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Source { get; set; }

    // Carries CropCategories.Code verbatim (VEG / FRT / VEG-UP / VEG-LOW). The FE rolls the sub-codes
    // under "Vegetables" itself — do not roll up server-side. Null only if the crop has no category.
    public CropCategory_GetDto? Category { get; set; }

    // GrowthPeriodDays, exposed only when the forecaster would use it (verified and > 0); otherwise null.
    // Mirrors the Python serving gate so the UI never shows a growth period the model refuses to use.
    public int? GrowthDays { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

// Nested taxonomy projection. Code is the verbatim CropCategories.Code; the FE keys on it.
public class CropCategory_GetDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
}
