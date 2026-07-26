namespace AgriForecast.Domain.Enums;

// Category of a captured news event. Stored as int.
// The member names and integer values deliberately mirror PolicyType 0..8: the admin News page reuses the
// PolicyType label mapper for eventType, so any relabel or renumber here is a lockstep change with the FE
// mapper. These values are not model inputs yet, so there is no Python mirror to keep in sync.
public enum NewsEventType
{
    Subsidy = 0,
    ImportBan = 1,
    ExportBan = 2,
    PriceCeiling = 3,
    PriceFloor = 4,
    FertiliserSubsidy = 5,
    FuelPriceChange = 6,
    Other = 7,
    Budget = 8
}
