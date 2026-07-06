using System.ComponentModel.DataAnnotations;

namespace AgriForecast.Domain.Entities;

public class DefaultSetting
{
    public int Id { get; set; }
    
    [MaxLength(10)]
    public string Crop_Prefix { get; set; }
    public int? Crop_Padding { get; set; }
    public int? Crop_Code { get; set; }

    // R2 D-DF3: the Eco_* code-generator fields were dropped when the EconomicCenters CRUD stack
    // retired (a Dedicated Economic Centre is now a Markets row with IsEconomicCenter=1, coded via
    // the Mkt_* scheme below). The DefaultSettings.Eco_* columns are dropped in the same phase.

    // MKT###### scheme for manually-created Markets (mirrors Crop_*).
    // Seeded markets use fixed codes; this drives future CRUD-created markets.
    public string Mkt_Prefix { get; set; }
    public int? Mkt_Padding { get; set; }
    public int? Mkt_Code { get; set; }
}