namespace AgriForecast.Domain.Interfaces;

// Read-only access to the seeded CropCategories reference table. There is no CRUD path, so this exposes
// only the existence check the crop-create validator needs.
public interface ICropCategoryRepository
{
    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);
}
