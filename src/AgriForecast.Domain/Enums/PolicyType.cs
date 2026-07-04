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
    Other = 7,

    // A national budget measure (annual/interim budget). Budget effects ship as PolicyFlag rows
    // (HasData-seeded) under this type — there is NO BudgetEvent entity/ingestion service (user cut
    // them, DECISIONS.md 2026-07-04): the Python feature layer derives budget_fy_active via the
    // existing _attach_policy path. Enum-int convention kept (next value); adding a budget = a seed row.
    Budget = 8
}
