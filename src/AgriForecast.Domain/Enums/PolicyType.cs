namespace AgriForecast.Domain.Enums;

// Category of a national policy flag. Stored as int; the ML feature store buckets policy effects by it.
public enum PolicyType
{
    Subsidy = 0,
    ImportBan = 1,
    ExportBan = 2,
    PriceCeiling = 3,
    PriceFloor = 4,
    FertiliserSubsidy = 5,
    FuelPriceChange = 6,
    Other = 7,

    // Budget measures ship as seeded PolicyFlag rows under this type; there is no BudgetEvent entity, and
    // the Python feature layer derives budget_fy_active from the existing policy path.
    Budget = 8
}
