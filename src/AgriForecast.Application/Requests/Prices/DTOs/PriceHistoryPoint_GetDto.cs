namespace AgriForecast.Application.Requests.Prices.DTOs;

// One calendar day's observed low-high price envelope for a (crop, market) series — the response shape
// for GET /api/prices/crop/{cropId}/history, matching the FE PriceHistoryPoint interface.
// This is observed history, never a forecast, so both bounds must be real. Date is a yyyy-MM-dd string.
public class PriceHistoryPoint_GetDto
{
    public string Date { get; set; } = string.Empty;
    public decimal MinPrice { get; set; }
    public decimal MaxPrice { get; set; }
}
