using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

public interface IEconimicCenterRepository
{
    Task<EconomicCenter> Addasync(EconomicCenter economicCenter);
    Task<EconomicCenter> UpdateAsync(EconomicCenter economicCenter);
    Task<EconomicCenter> DeleteAsync(EconomicCenter economicCenter);
    Task<EconomicCenter?> GetByIdAsync(Guid id);
    Task<EconomicCenter?> GetByCodeAsync(string code);
    Task<IEnumerable<EconomicCenter>> GetAllAsync();
}