namespace AgriForecast.Domain.Enums;

// Category of a national policy flag. Stored as int.
// The ML feature store reads this to bucket policy effects per crop/commodity.
public enum PolicyType
{
    Subsidy = 0,
    ImportBan = 1,
    ExportBan = 2,
    PriceCeiling = 3,
    PriceFloor = 4,
    FertiliserSubsidy = 5,
    FuelPriceChange = 6,
    Other = 7
}
