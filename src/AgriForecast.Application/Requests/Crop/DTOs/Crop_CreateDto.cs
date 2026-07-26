namespace AgriForecast.Application.Requests.Crop.DTOs;

public class Crop_CreateDto
{
    public string Name { get;  set; }
    public string? Source { get; set; }

    // Required: a manual crop must be filed under an existing CropCategory (validated NotEmpty and must
    // exist). The ingestion auto-provision path instead defaults the category.
    public Guid CropCategoryId { get; set; }
}