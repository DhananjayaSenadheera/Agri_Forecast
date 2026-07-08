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

    // R2 D-DF4: crops are coded per TOP-LEVEL category prefix (VEG######/FRT######), assign-once.
    // The caller resolves the prefix from the crop's CropCategoryId via CropCategory.PrefixForCategory
    // (Up/Low-country Vegetable roll up to VEG). Unknown prefixes default to the VEG counter.
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