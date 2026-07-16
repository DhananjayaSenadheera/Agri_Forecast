using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

public interface IPolicyFlagRepository
{
    Task<PolicyFlag> AddAsync(PolicyFlag policyFlag);
    Task<PolicyFlag> UpdateAsync(PolicyFlag policyFlag);
    Task DeleteAsync(PolicyFlag policyFlag);
    Task<PolicyFlag?> GetByIdAsync(Guid id);

    // All flags, ordered by EffectiveFrom (chronological).
    Task<IEnumerable<PolicyFlag>> GetAllAsync();

    // Flags active on the given date: EffectiveFrom <= asOf AND (EffectiveTo is null OR asOf <= EffectiveTo).
    Task<IEnumerable<PolicyFlag>> GetActiveAsOfAsync(DateTime asOf);
}
