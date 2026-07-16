namespace AgriForecast.Application.Requests.Prices.DTOs;

// One calendar day's observed low-high price envelope for a (crop, market) series.
// Response shape for GET /api/prices/crop/{cropId}/history — EXACTLY the FE
// `PriceHistoryPoint` interface (src/api/types.ts): { date, minPrice, maxPrice }.
//
// This is OBSERVED history, never a forecast: the FE renders it as a daily low-high
// range band, so both bounds must be real. Date is a yyyy-MM-dd string (existing DTO
// idiom); prices are decimal.
public class PriceHistoryPoint_GetDto
{
    public string Date { get; set; } = string.Empty;
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }
}
