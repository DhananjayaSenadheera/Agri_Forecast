namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastSnapshots;

// One ForecastSnapshots row for GET /api/admin/forecast-accuracy/snapshots. Date-only columns are
// yyyy-MM-dd strings and instants are UTC-stamped DateTimes, matching the other admin lists.
//
// The prediction fields are the FROZEN record of what was actually served — including a Low-confidence
// fallback. They are passed through verbatim: nothing here is re-derived, re-scored or presented as
// more certain than it was on the day.
public class ForecastSnapshot_GetDto
{
    public Guid Id { get; set; }
    public Guid CropId { get; set; }
    public string CropName { get; set; } = string.Empty;
    public string? CropCode { get; set; }

    public string SnapshotDate { get; set; } = string.Empty; // yyyy-MM-dd — the plant date
    public string? HarvestDate { get; set; }                 // yyyy-MM-dd, null on a not_maturable row
    public int? GrowthPeriodDays { get; set; }

    public decimal PredictedPrice { get; set; }
    public decimal LowerBound { get; set; }
    public decimal UpperBound { get; set; }

    // The price known on the plant date; the anchor directional accuracy is measured against. Null when
    // no anchor existed, which is why some rows are excluded from that metric.
    public decimal? ReferencePrice { get; set; }

    // "Low" | "Medium" | "High", verbatim as served — never upgraded.
    public string Confidence { get; set; } = string.Empty;

    public string ActivePredictor { get; set; } = string.Empty;
    public string? FallbackTier { get; set; }
    public string? ModelVersion { get; set; }
    public string? ReasonCode { get; set; }

    // One of ForecastSnapshotMaturityStates: "pending" | "matured" | "actual_unavailable" |
    // "not_maturable". Lowercase, exactly as stored and as the Python job writes it.
    public string MaturityState { get; set; } = string.Empty;

    public decimal? ActualPrice { get; set; }
    public string? ActualObservedDate { get; set; } // yyyy-MM-dd — audits which trading day was used

    // Filled only on a matured row. signedError/absoluteError are Rs/kg; percentageError is SIGNED and
    // in PERCENT units. Read as stored, never recomputed on this path.
    public decimal? SignedError { get; set; }
    public decimal? AbsoluteError { get; set; }
    public decimal? PercentageError { get; set; }
    public bool? WithinInterval { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    // The instant the row reached a terminal state (matured OR given up on). Null while pending.
    public DateTime? MaturedAtUtc { get; set; }
}
