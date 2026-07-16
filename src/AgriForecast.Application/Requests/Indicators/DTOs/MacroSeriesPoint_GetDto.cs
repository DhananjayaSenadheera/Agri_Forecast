namespace AgriForecast.Application.Requests.Indicators.DTOs;

// Response element for GET /api/macro-series?key=&from=&to= (MacroSeriesPoints, e.g. CCPI).
// Wire shape matches the FE proposal `MacroSeriesPoint` in ForecastUI src/api/types.ts, so
// the FE flip off fixtures is a client-method swap. camelCase JSON: seriesKey, referenceDate,
// publishedAt, value, source.
//
// NAMING: the JSON field is `seriesKey` (the FE's chosen name), mapped from the entity's
// `SeriesCode`. This is a deliberate wire-name alignment, not a rename of the domain concept.
//
// LEAKAGE GUARD (LOAD-BEARING): referenceDate and publishedAt are ALWAYS both present and
// distinct — the two-date discipline. `referenceDate` = the period the figure describes;
// `publishedAt` = when it became knowable (the release vintage). NEVER collapse them onto one
// field, and never map publishedAt onto referenceDate (that is lookahead / leakage).
public class MacroSeriesPoint_GetDto
{
    public string SeriesKey { get; set; } = string.Empty;     // e.g. "CCPI_BASE2021" (entity SeriesCode)
    public string ReferenceDate { get; set; } = string.Empty; // yyyy-MM-dd — period described (chart axis)
    public string PublishedAt { get; set; } = string.Empty;   // yyyy-MM-dd — when knowable (vintage)
    public decimal Value { get; set; }
    public string Source { get; set; } = string.Empty;
}
