namespace AgriForecast.Application.Requests.Indicators.DTOs;

// Response element for GET /api/indicators?code=&from=&to= (EconomicIndicators, e.g. USD_LKR).
// Wire shape matches the FE proposal `DailyIndicatorPoint` in ForecastUI src/api/types.ts, so
// the FE flip off fixtures is a client-method swap. camelCase JSON (API default): date,
// indicatorCode, value, source. Date is a yyyy-MM-dd string; value is a JSON number.
public class DailyIndicatorPoint_GetDto
{
    public string Date { get; set; } = string.Empty;      // yyyy-MM-dd — the reading's date
    public string IndicatorCode { get; set; } = string.Empty; // e.g. "USD_LKR"
    public decimal Value { get; set; }
    public string Source { get; set; } = string.Empty;
}
