namespace AgriForecast.Domain.Interfaces;

// Read-only access to the CropCategories reference dimension. CropCategories is a
// seeded, fixed-GUID reference table (no CRUD path), so this exposes only the
// existence check the manual crop-create validator needs to reject unknown categories.
public interface ICropCategoryRepository
{
    // True if a CropCategory row with this Id exists (top-level or sub-category).
    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);
}
