using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

public interface ICropRepository
{
    Task<Crop> Addasync(Crop crop);
    Task<Crop> UpdateAsync(Crop crop);
    Task<Crop> DeleteAsync(Crop crop);
    Task<Crop?> GetByIdAsync(Guid id);
    Task<Crop?> GetByCodeAsync(string code);
    Task<IEnumerable<Crop>> GetAllAsync();
}