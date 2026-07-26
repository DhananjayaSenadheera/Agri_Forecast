using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;

namespace AgriForecast.Application.common;

public class CodeSettings: Result<string>
{
    private readonly IDefaultSettingRepository _defaultSettingRepository;
    
    public CodeSettings(IDefaultSettingRepository defaultSettingRepository)
    {
        _defaultSettingRepository = defaultSettingRepository;
    }

    // Crops are coded per top-level category prefix (VEG######/FRT######), assigned once. The caller
    // resolves the prefix via CropCategory.PrefixForCategory; an unknown prefix uses the VEG counter.
    public async Task<string?> GetCropCode(string prefix)
    {
        var defaultSetting = await _defaultSettingRepository.GetDefaultSetting();

        var isFruit = string.Equals(prefix, CropCategory.FruitPrefix, StringComparison.OrdinalIgnoreCase);

        var codePrefix = isFruit ? defaultSetting.Frt_Prefix : defaultSetting.Veg_Prefix;
        var counter = isFruit ? defaultSetting.Frt_Code : defaultSetting.Veg_Code;
        var padding = isFruit ? defaultSetting.Frt_Padding : defaultSetting.Veg_Padding;

        var cropCode = codePrefix + counter.ToString()?.PadLeft((int)padding!, '0');
        if (string.IsNullOrEmpty(cropCode))
            return null;

        if (isFruit)
            defaultSetting.Frt_Code += 1;
        else
            defaultSetting.Veg_Code += 1;

        _defaultSettingRepository.UpdateDefaultSetting(defaultSetting);
        return cropCode;
    }

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