using System.ComponentModel.DataAnnotations;

namespace AgriForecast.Domain.Entities;

public class DefaultSetting
{
    public int Id { get; set; }

    // Per-prefix crop-code counters (VEG######/FRT######). CropCode is display-only — no unique index,
    // FK or join — so a counter race can at worst duplicate a cosmetic code.
    [MaxLength(10)]
    public string Veg_Prefix { get; set; }
    public int? Veg_Padding { get; set; }
    public int? Veg_Code { get; set; }

    [MaxLength(10)]
    public string Frt_Prefix { get; set; }
    public int? Frt_Padding { get; set; }
    public int? Frt_Code { get; set; }

    // MKT###### scheme for markets created through CRUD; seeded markets use fixed codes.
    public string Mkt_Prefix { get; set; }
    public int? Mkt_Padding { get; set; }
    public int? Mkt_Code { get; set; }
}