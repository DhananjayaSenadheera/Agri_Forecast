using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;

namespace AgriForecast.Infrastructure.Repositories;

public class EconimicCenterRepository : IEconimicCenterRepository
{
    private readonly IGenericRepository<EconomicCenter> _economicCenterRepository;
    
    public EconimicCenterRepository(IGenericRepository<EconomicCenter> economicCenterRepository)
    {
        _economicCenterRepository = economicCenterRepository;
    }
    
    public async Task<EconomicCenter> Addasync(EconomicCenter economicCenter)
    {
        await _economicCenterRepository.AddAsync(economicCenter);
        return economicCenter;
    }

    public async Task<EconomicCenter> UpdateAsync(EconomicCenter economicCenter)
    {
        await _economicCenterRepository.UpdateAsync(economicCenter);
        return economicCenter;
    }

    public async Task<EconomicCenter> DeleteAsync(EconomicCenter economicCenter)
    {
        await _economicCenterRepository.DeleteAsync(economicCenter);
        return economicCenter;
    }

    public async Task<EconomicCenter?> GetByIdAsync(Guid id)
    {
       var result = await _economicCenterRepository.GetByIdAsync(id);
         return result;
    }

    public async Task<EconomicCenter?> GetByCodeAsync(string code)
    {
        var result = await _economicCenterRepository.GetByCodeAsync(code);
        return result;
    }

    public async Task<IEnumerable<EconomicCenter>> GetAllAsync()
    {
       var result = await _economicCenterRepository.GetAllAsync();
        return result;
    }
}