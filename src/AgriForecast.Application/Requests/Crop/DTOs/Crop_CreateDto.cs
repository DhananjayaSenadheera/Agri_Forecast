namespace AgriForecast.Application.Requests.Crop.DTOs;

public class Crop_CreateDto
{
    public string Name { get;  set; }
    public string? Source { get; set; }

    // Required grouping under a CropCategory (Vegetable / Fruit + sub-categories).
    // Validated NotEmpty AND must exist in CropCategories. A crop created here is a
    // manual/curated entry, so the category is chosen explicitly (unlike the ingestion
    // auto-provision path, which defaults to top-level Vegetable / Fruit-by-keyword).
    public Guid CropCategoryId { get; set; }
}