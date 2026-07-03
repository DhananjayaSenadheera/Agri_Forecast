using System.ComponentModel.DataAnnotations;

namespace AgriForecast.Domain.Entities;

public class DefaultSetting
{
    public int Id { get; set; }
    
    [MaxLength(10)]
    public string Crop_Prefix { get; set; }
    public int? Crop_Padding { get; set; }
    public int? Crop_Code { get; set; }
    
    public string Eco_Prefix { get; set; }
    public int? Eco_Padding { get; set; }
    public int? Eco_Code { get; set; }

    // MKT###### scheme for manually-created Markets (mirrors Crop_*/Eco_*).
    // Seeded markets use fixed codes; this drives future CRUD-created markets.
    public string Mkt_Prefix { get; set; }
    public int? Mkt_Padding { get; set; }
    public int? Mkt_Code { get; set; }
}