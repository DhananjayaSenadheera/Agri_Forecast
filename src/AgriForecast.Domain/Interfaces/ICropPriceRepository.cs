using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

public interface ICropPriceRepository
{
    Task AddAsync(CropPrice cropPrice, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid cropId, Guid economicCenterId, DateTime month, CancellationToken ct = default);
    Task<List<CropPrice>> GetByCropIdAsync(Guid cropId, CancellationToken ct = default);
}
