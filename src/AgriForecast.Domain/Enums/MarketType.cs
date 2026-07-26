namespace AgriForecast.Domain.Enums;

// Category of a market. Stored as int.
// DEC = Dedicated Economic Centre. Wholesale/Retail = HARTI pseudo-markets that publish bulletins for a
// location without being a trading floor we ingest directly. NationalAggregate = a synthetic market
// holding an already-averaged national figure; it is not a location and must never be mixed into
// location-level cross-market aggregation.
public enum MarketType
{
    Wholesale = 0,
    Retail = 1,
    DEC = 2,
    NationalAggregate = 3
}
