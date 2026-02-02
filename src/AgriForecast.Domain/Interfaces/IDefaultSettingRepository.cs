using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

public interface IDefaultSettingRepository
{
    Task<DefaultSetting> GetDefaultSetting();
    
    void UpdateDefaultSetting(DefaultSetting defaultSetting);
}