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
}