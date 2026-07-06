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

    // R2 D-DF3: GetEcoCode() was removed with the EconomicCenters CRUD retirement. Economic-centre
    // registration is now "create a Market with IsEconomicCenter=true", coded via GetMktCode().
    public async Task<string?> GetMktCode()
    {
        var defaultSetting = await _defaultSettingRepository.GetDefaultSetting();
        var mktCode = defaultSetting.Mkt_Prefix
                      + defaultSetting.Mkt_Code.ToString()?.PadLeft((int)defaultSetting.Mkt_Padding!, '0');
        if (string.IsNullOrEmpty(mktCode))
            return null;
        defaultSetting.Mkt_Code += 1;
        _defaultSettingRepository.UpdateDefaultSetting(defaultSetting);
        return mktCode;
    }

}