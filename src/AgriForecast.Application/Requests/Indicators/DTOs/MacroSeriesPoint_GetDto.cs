namespace AgriForecast.Application.Requests.Indicators.DTOs;

// Response element for GET /api/macro-series. Matches the FE MacroSeriesPoint interface.
// The JSON field is seriesKey (the FE's chosen name), mapped from the entity's SeriesCode.
// referenceDate and publishedAt are always both present and distinct: referenceDate is the period the
// figure describes, publishedAt is when it became knowable. Never collapse them onto one field and never
// map publishedAt onto referenceDate — that is lookahead.
public class MacroSeriesPoint_GetDto
{
    public string SeriesKey { get; set; } = string.Empty;     // e.g. "CCPI_BASE2021" (entity SeriesCode)
    public string ReferenceDate { get; set; } = string.Empty; // yyyy-MM-dd — period described (chart axis)
    public string PublishedAt { get; set; } = string.Empty;   // yyyy-MM-dd — when knowable (vintage)
    public decimal Value { get; set; }
    public string Source { get; set; } = string.Empty;
}
