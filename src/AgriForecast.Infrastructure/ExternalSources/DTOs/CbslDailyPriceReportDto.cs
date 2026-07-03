namespace AgriForecast.Infrastructure.ExternalSources.DTOs;

// One parsed row of the CBSL Daily Price Report (R1.1 P1 Step 6, CBSL skeleton).
//
// POINT-IN-TIME CONTRACT: PublishedAtUtc is the report's own publication vintage (the date/time
// the CBSL bulletin was issued), NEVER ingestion "now". It maps to PriceObservation.AsOfUtc.
// A CBSL report published on day D carrying prices FOR day D-1 must keep ObservedDate (D-1) and
// PublishedAtUtc (D) distinct — availability-late is safe, availability-early is leakage.
//
// The CBSL report publishes a SINGLE point figure per series (not a Min/Max range like HARTI),
// so WholesalePrice/RetailPrice carry the value and Min/Max stay null — the inverse of the HARTI
// convention. This DTO is the .NET-side shape the (future, Python-owned) parser will populate;
// see CbslPriceReportIngestionService for why the parse itself is not implemented here yet.
public sealed class CbslDailyPriceReportDto
{
    // Commodity label exactly as the CBSL report states it (resolved to a Crop via the
    // canonical CommodityAlias layer downstream — never auto-created).
    public string CommodityLabel { get; init; } = "";

    // The date the price is FOR (the economic event date).
    public DateOnly ObservedDate { get; init; }

    // The report's publication vintage — maps to PriceObservation.AsOfUtc. Required; must be the
    // published-at timestamp, never ingestion now.
    public DateTime PublishedAtUtc { get; init; }

    // CBSL point figures. Nullable so a partial report row lands cleanly.
    public decimal? WholesalePrice { get; init; }
    public decimal? RetailPrice { get; init; }

    // The unit exactly as the report stated it (fed into the unit-quarantine path; never assumed).
    public string? UnitRaw { get; init; }
}
