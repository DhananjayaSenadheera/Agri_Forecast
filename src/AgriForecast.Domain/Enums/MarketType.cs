namespace AgriForecast.Domain.Enums;

// Category of a market as a first-class dimension. Stored as int.
// DEC              = Dedicated Economic Centre (government wholesale hubs, e.g. Dambulla).
// Wholesale/Retail = HARTI pseudo-markets that publish price bulletins for a specific
//                    trading location without being a physical trading floor we ingest directly.
// NationalAggregate= a synthetic pseudo-market carrying an already-averaged national figure
//                    (e.g. CBSL national average). It is NOT a location and must never be
//                    mixed into location-level cross-market aggregation.
public enum MarketType
{
    Wholesale = 0,
    Retail = 1,
    DEC = 2,
    NationalAggregate = 3
}
