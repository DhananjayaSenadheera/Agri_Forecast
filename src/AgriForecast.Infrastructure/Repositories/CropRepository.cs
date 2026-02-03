using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;

namespace AgriForecast.Infrastructure.Repositories;

public class CropRepository : ICropRepository
{
    private readonly IGenericRepository<Crop> _cropRepository;
    
    public CropRepository(IGenericRepository<Crop> cropRepository)
    {
        _cropRepository = cropRepository;
    }
    
    public async Task<Crop> Addasync(Crop crop)
    {
        await _cropRepository.AddAsync(crop);
        return crop;
    }

    public async Task<Crop> UpdateAsync(Crop crop)
    {
        await _cropRepository.UpdateAsync(crop);
        return crop;
    }

    public async Task<Crop> DeleteAsync(Crop crop)
    {
        await _cropRepository.DeleteAsync(crop);
        return crop;
    }

    public async Task<Crop?> GetByIdAsync(Guid id)
    {
       return await _cropRepository.GetByIdAsync(id);
    }

    public async Task<Crop?> GetByCodeAsync(string code)
    {
        return await _cropRepository.GetByCodeAsync(code);
    }

    public async Task<IEnumerable<Crop>> GetAllAsync()
    {
        return await _cropRepository.GetAllAsync();
    }
}