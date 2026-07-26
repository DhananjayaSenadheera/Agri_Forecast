namespace AgriForecast.Application.Requests.Indicators.DTOs;

// Response element for GET /api/indicators. Matches the FE DailyIndicatorPoint interface; date is a
// yyyy-MM-dd string and value is a JSON number.
public class DailyIndicatorPoint_GetDto
{
    public string Date { get; set; } = string.Empty;      // yyyy-MM-dd — the reading's date
    public string IndicatorCode { get; set; } = string.Empty; // e.g. "USD_LKR"
    public decimal Value { get; set; }
    public string Source { get; set; } = string.Empty;
}
