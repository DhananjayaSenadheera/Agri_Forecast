using System.ComponentModel.DataAnnotations;

namespace AgriForecast.Domain.Entities;

public class DefaultSetting
{
    public int Id { get; set; }
    
    // R2 D-DF4: the single legacy Crop_* counter is retired in favour of per-category-prefix
    // counters below. Crops are now coded VEG######/FRT###### (prefix = category at registration),
    // so one shared counter can no longer serve both prefixes. Columns dropped in the same phase.

    // Per-prefix crop-code counters (VEG######/FRT######, assign-once, category-prefixed).
    // Seeded to next-free (Veg=71 after 70 existing VEG crops, Frt=27 after 26 FRT), padding 6.
    // CropCode is display-only (no unique index / FK / join) — a rare counter race would at worst
    // duplicate a cosmetic code, never break an insert.
    [MaxLength(10)]
    public string Veg_Prefix { get; set; }
    public int? Veg_Padding { get; set; }
    public int? Veg_Code { get; set; }

    [MaxLength(10)]
    public string Frt_Prefix { get; set; }
    public int? Frt_Padding { get; set; }
    public int? Frt_Code { get; set; }

    // R2 D-DF3: the Eco_* code-generator fields were dropped when the EconomicCenters CRUD stack
    // retired (a Dedicated Economic Centre is now a Markets row with IsEconomicCenter=1, coded via
    // the Mkt_* scheme below). The DefaultSettings.Eco_* columns are dropped in the same phase.

    // MKT###### scheme for manually-created Markets (mirrors Crop_*).
    // Seeded markets use fixed codes; this drives future CRUD-created markets.
    public string Mkt_Prefix { get; set; }
    public int? Mkt_Padding { get; set; }
    public int? Mkt_Code { get; set; }
}