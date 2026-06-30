using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

public class PolicyFlagRepository : IPolicyFlagRepository
{
    private readonly AgriForecastDbContext _db;

    public PolicyFlagRepository(AgriForecastDbContext db)
    {
        _db = db;
    }

    public async Task<PolicyFlag> AddAsync(PolicyFlag policyFlag)
    {
        await _db.PolicyFlags.AddAsync(policyFlag);
        return policyFlag;
    }

    public async Task<PolicyFlag?> GetByIdAsync(Guid id)
    {
        return await _db.PolicyFlags.FindAsync(id);
    }

    public async Task<IEnumerable<PolicyFlag>> GetAllAsync()
    {
        return await _db.PolicyFlags
            .AsNoTracking()
            .OrderBy(p => p.EffectiveFrom)
            .ToListAsync();
    }

    public async Task<IEnumerable<PolicyFlag>> GetActiveAsOfAsync(DateTime asOf)
    {
        var day = asOf.Date;
        return await _db.PolicyFlags
            .AsNoTracking()
            .Where(p => p.EffectiveFrom <= day && (p.EffectiveTo == null || day <= p.EffectiveTo))
            .OrderBy(p => p.EffectiveFrom)
            .ToListAsync();
    }
}
