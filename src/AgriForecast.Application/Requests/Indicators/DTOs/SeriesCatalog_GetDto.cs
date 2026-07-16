namespace AgriForecast.Application.Requests.Indicators.DTOs;

// Response element for GET /api/indicators/catalog — the series picker's directory across BOTH
// data sources. camelCase JSON: key, kind, latestDate, count.
//
// One unified list (not two) with a `kind` discriminator so a single admin picker can offer
// every series and know which endpoint / client method to call: kind "indicator" -> the daily
// EconomicIndicators route (GET /api/indicators?code=key); kind "macro" -> the vintage-aware
// MacroSeriesPoints route (GET /api/macro-series?key=key). The Indicators page currently
// hardcodes CCPI_BASE2021; this route replaces that with a real, data-driven picker.
public class SeriesCatalog_GetDto
{
    public string Key { get; set; } = string.Empty;        // IndicatorCode or SeriesCode
    public string Kind { get; set; } = string.Empty;       // "indicator" | "macro"
    public string LatestDate { get; set; } = string.Empty; // yyyy-MM-dd (max Date / max ReferenceDate)
    public int Count { get; set; }                          // number of rows in the series
}
