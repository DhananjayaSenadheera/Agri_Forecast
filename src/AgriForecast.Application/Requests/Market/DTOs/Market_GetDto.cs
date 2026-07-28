using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Market.DTOs;

// Response row for GET /api/markets/get/all, consumed by the farmer Prices page and the admin registry.
// This API registers no JsonStringEnumConverter, so MarketType serializes as an INTEGER (0 Wholesale /
// 1 Retail / 2 DEC / 3 NationalAggregate), which is what the FE expects. Never add the converter.
public class Market_GetDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    // Short display code shown beside the name (e.g. "DEC", "KEP"). Display-only — the FE must keep
    // keying on Id. Empty string (never null) when a market has no code assigned yet.
    public string ShortCode { get; set; } = string.Empty;
    // Nullable: the CBSL NationalAggregate pseudo-market carries no district.
    public string? District { get; set; }
    public MarketType MarketType { get; set; }
    public bool IsEconomicCenter { get; set; }

    // Whether the market stores any price observations at all, of any status.
    public bool HasStoredData { get; set; }
    // Most recent observed date of its stored data; null when nothing is stored. Serializes as yyyy-MM-dd.
    public DateOnly? LastStoredDate { get; set; }
    // Whether this market currently feeds model training (feature-safe + usable data).
    public bool IsTrainingSource { get; set; }
}
