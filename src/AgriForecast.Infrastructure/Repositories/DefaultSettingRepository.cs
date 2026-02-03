using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;

namespace AgriForecast.Infrastructure.Repositories;

public class DefaultSettingRepository : IDefaultSettingRepository
{
    private readonly IGenericRepository<DefaultSetting> _genericRepository;

    public DefaultSettingRepository(IGenericRepository<DefaultSetting> genericRepository)
    {
        _genericRepository = genericRepository;
    }
    
    public async Task<DefaultSetting> GetDefaultSetting()
    {
        var result = await _genericRepository.GetoneAsync();
        return result ;
    }

    public void UpdateDefaultSetting(DefaultSetting defaultSetting)
    {
        _genericRepository.UpdateAsync(defaultSetting);
    }
}