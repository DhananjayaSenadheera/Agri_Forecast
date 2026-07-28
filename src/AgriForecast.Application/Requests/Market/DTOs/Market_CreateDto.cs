using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Market.DTOs;

// Manual market registration. Registering a Dedicated Economic Centre means posting this with
// IsEconomicCenter = true; a plain market leaves it at the default false.
public class Market_CreateDto
{
    public string Name { get; set; } = string.Empty;

    // District is the location label (nullable, mirrors Market.District).
    public string? District { get; set; }

    // Validated to be a defined MarketType value.
    public MarketType MarketType { get; set; }

    // Defaults false. Set true to register a Dedicated Economic Centre.
    public bool IsEconomicCenter { get; set; }

    // Optional short display code (e.g. "KEP"). Upper-cased on the way in and unique among assigned
    // codes; omitting it registers the market without a display code rather than inventing one.
    public string? ShortCode { get; set; }
}
