using AgriForecast.Domain.Interfaces;

namespace AgriForecast.Application.common;

public class CodeSettings: Result<string>
{
    private readonly IDefaultSettingRepository _defaultSettingRepository;
    
    public CodeSettings(IDefaultSettingRepository defaultSettingRepository)
    {
        _defaultSettingRepository = defaultSettingRepository;
    }

    public async Task<string?> GetCropCode()
    {
        var defaultSetting = await _defaultSettingRepository.GetDefaultSetting();
        var cropCode = defaultSetting.Crop_Prefix 
                       + defaultSetting.Crop_Code.ToString()?.PadLeft((int)defaultSetting.Crop_Padding!, '0');
        if (string.IsNullOrEmpty(cropCode))
            return null;
        defaultSetting.Crop_Code += 1;
        _defaultSettingRepository.UpdateDefaultSetting(defaultSetting);
        return cropCode;
    }
    
    public async Task<string?> GetEcoCode()
    {
        var defaultSetting = await _defaultSettingRepository.GetDefaultSetting();
        var ecoCode = defaultSetting.Eco_Prefix + defaultSetting.Eco_Code.ToString()?.PadLeft((int)defaultSetting.Eco_Padding!, '0');
        if (string.IsNullOrEmpty(ecoCode))
            return null;
        defaultSetting.Eco_Code += 1;
        _defaultSettingRepository.UpdateDefaultSetting(defaultSetting);
        return ecoCode;
    }
}