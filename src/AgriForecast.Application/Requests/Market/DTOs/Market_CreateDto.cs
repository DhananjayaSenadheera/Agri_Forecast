using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Market.DTOs;

// Manual/curated market registration DTO (R2 D-DF3 — the replacement for the retired
// EconomicCenters CRUD create). "Register a new economic centre" = POST this with
// IsEconomicCenter = true; a plain market leaves it at the default false.
public class Market_CreateDto
{
    public string Name { get; set; } = string.Empty;

    // District is the location label (nullable, mirrors Market.District).
    public string? District { get; set; }

    // Category of the market (Wholesale/Retail/DEC/NationalAggregate). Validated to be a
    // defined MarketType value.
    public MarketType MarketType { get; set; }

    // Optional — defaults false. Set true to register a Dedicated Economic Centre
    // (folds the retired EconomicCenters dimension into Markets via the IsEconomicCenter flag).
    public bool IsEconomicCenter { get; set; }
}
