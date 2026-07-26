namespace AgriForecast.Application.Requests.Indicators.DTOs;

// Response element for GET /api/indicators/catalog — the series picker's directory across both data
// sources. One unified list with a kind discriminator so a single picker knows which route to call:
// "indicator" -> GET /api/indicators?code=, "macro" -> GET /api/macro-series?key=.
public class SeriesCatalog_GetDto
{
    public string Key { get; set; } = string.Empty;        // IndicatorCode or SeriesCode
    public string Kind { get; set; } = string.Empty;       // "indicator" | "macro"
    public string LatestDate { get; set; } = string.Empty; // yyyy-MM-dd (max Date / max ReferenceDate)
    public int Count { get; set; }                          // number of rows in the series
}
