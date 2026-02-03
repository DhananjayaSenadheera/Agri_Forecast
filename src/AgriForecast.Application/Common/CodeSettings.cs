using AgriForecast.Domain.Interfaces;

namespace AgriForecast.Application.common;

public class CodeSettings
{
    private readonly IDefaultSettingRepository _defaultSettingRepository;
    
    public CodeSettings(IDefaultSettingRepository defaultSettingRepository)
    {
        _defaultSettingRepository = defaultSettingRepository;
    }

    public async Task<string> GetCropCode()
    {
        var defaultSetting = await _defaultSettingRepository.GetDefaultSetting();
        var cropCode = defaultSetting.Crop_Prefix + defaultSetting.Crop_Code.ToString()?.PadLeft((int)defaultSetting.Crop_Padding!);
        return cropCode;
    }
    
    public void UpdateCropCode()
    {
        var defaultSetting =  _defaultSettingRepository.GetDefaultSetting().Result;
        defaultSetting.Crop_Code += 1;
        _defaultSettingRepository.UpdateDefaultSetting(defaultSetting);
    }
    
    public async Task<string> GetEcoCode()
    {
        var defaultSetting = await _defaultSettingRepository.GetDefaultSetting();
        var Eco_code = defaultSetting.Eco_Prefix + defaultSetting.Eco_Code.ToString()?.PadLeft((int)defaultSetting.Eco_Padding!);
        return Eco_code;
    }
    public void UpdateEcoCode()
    {
        var defaultSetting =  _defaultSettingRepository.GetDefaultSetting().Result;
        defaultSetting.Eco_Code += 1;
        _defaultSettingRepository.UpdateDefaultSetting(defaultSetting);
    }
}