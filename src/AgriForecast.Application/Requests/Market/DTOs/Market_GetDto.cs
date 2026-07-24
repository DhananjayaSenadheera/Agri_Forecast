using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Requests.Market.DTOs;

// Response row for GET /api/markets/get/all. Contract is consumed by the farmer
// Prices page + the admin market registry (FE `Market` interface in src/api/types.ts).
//
// SERIALIZATION (load-bearing): this API registers NO JsonStringEnumConverter, so
// MarketType serializes as an INTEGER on the wire (0 Wholesale / 1 Retail / 2 DEC /
// 3 NationalAggregate) — exactly what the FE `marketType: number` field expects.
// Never add a JsonStringEnumConverter: it would flip this to a string and break the FE.
public class Market_GetDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    // Nullable: the CBSL NationalAggregate pseudo-market carries no district. The FE
    // treats district as `string | null` and degrades gracefully when absent.
    public string? District { get; set; }
    public MarketType MarketType { get; set; }
    public bool IsEconomicCenter { get; set; }

    // --- Monitoring status (admin Markets registry) ---
    // Whether the market stores any price observations at all (any status).
    public bool HasStoredData { get; set; }
    // Most recent observed date of its stored data; null when nothing is stored.
    // DateOnly serializes as "yyyy-MM-dd" (no JsonStringEnumConverter here); the FE
    // renders it with formatDate() and treats null as "never".
    public DateOnly? LastStoredDate { get; set; }
    // Whether this market currently feeds model training (feature-safe + usable data).
    public bool IsTrainingSource { get; set; }
}
