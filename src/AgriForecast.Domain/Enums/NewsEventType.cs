namespace AgriForecast.Domain.Enums;

// Category of a captured news event. Stored as int (no JsonStringEnumConverter — same wire
// discipline as PolicyType/PolicyDirection).
//
// DELIBERATE COUPLING: the member names + integer values MIRROR PolicyType 0..8 exactly. The
// ForecastUI admin News page (ADM-7) reuses the tested `mapPolicyType` label mapper for
// eventType (see ForecastUI src/api/types.ts NewsEvent.eventType + src/lib/format.ts), so the
// integer contract MUST match PolicyType or the FE renders the wrong label. A separate enum
// (rather than reusing PolicyType) is kept because a news event is a distinct concept from a
// government policy flag and its label set will diverge later — but any relabel/renumber here
// is a LOCKSTEP change with the FE mapper. This is capture+storage only: these values are NOT
// yet model inputs (the model will learn event weights in a later, separate task), so there is
// no Python mirror to keep in sync yet.
public enum NewsEventType
{
    Subsidy = 0,
    ImportBan = 1,
    ExportBan = 2,
    PriceCeiling = 3,
    PriceFloor = 4,
    FertiliserSubsidy = 5,
    FuelPriceChange = 6,
    Other = 7,
    Budget = 8
}
