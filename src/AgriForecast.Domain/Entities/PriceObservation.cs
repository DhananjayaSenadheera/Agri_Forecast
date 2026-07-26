namespace AgriForecast.Domain.Entities;

// Point-in-time price observation; supersedes MarketPrice, which is kept for back-compat. One row is one
// commodity's price at one market for one ObservedDate as published by one Source. All price fields are
// nullable so partial bulletins insert cleanly.
//
// ObservedDate is the date the price is FOR; AsOfUtc is when the bulletin carrying it was published and
// is what the ML layer as-of-joins on; RetrievedAtUtc is audit only. AsOfUtc is required and rejected if
// default(DateTime), because a 0001-01-01 vintage would look already-published in every as-of window.
public class PriceObservation
{
    public Guid Id { get; private set; }

    // FK -> Markets. Required: every observation belongs to a market dimension.
    public Guid MarketId { get; private set; }

    // Nullable: back-filled later by the canonical commodity mapping, as MarketPrice.CropId is.
    public Guid? CropId { get; private set; }

    // Source-native identity. ExternalCommodityId is null for sources that key on name only.
    public int? ExternalCommodityId { get; private set; }
    public string ExternalCommodityName { get; private set; } = string.Empty;

    // The date the price is FOR (date-only, no hidden time).
    public DateOnly ObservedDate { get; private set; }

    // Prices — all nullable so partial bulletins insert cleanly. decimal(10,2).
    public decimal? WholesalePrice { get; private set; }
    public decimal? RetailPrice { get; private set; }
    public decimal? MinPrice { get; private set; }
    public decimal? MaxPrice { get; private set; }

    // Arrivals volume in kilograms. decimal(12,2).
    public decimal? ArrivalsKg { get; private set; }

    // Unit provenance, fail-closed. Sources state prices in different units, so UnitRaw keeps the source's
    // own wording, UnitConversionFactor is the factor to canonical LKR/kg (1.0 = already LKR/kg), and
    // IsUnitConfirmed stays false until ingestion proves the unit. Aggregation and feature layers must
    // exclude unconfirmed rows rather than assume LKR/kg.
    public string? UnitRaw { get; private set; }
    public decimal? UnitConversionFactor { get; private set; }
    public bool IsUnitConfirmed { get; private set; }

    // Bulletin publication timestamp — the point-in-time vintage (distinct from ObservedDate).
    public DateTime AsOfUtc { get; private set; }

    // Provenance, e.g. "HARTI", "CBSL", "DEC-DAMBULLA".
    public string Source { get; private set; } = string.Empty;

    // Audit only; never a feature.
    public DateTime RetrievedAtUtc { get; private set; }

    private PriceObservation() { }

    // Throws if asOfUtc is default(DateTime): a missing vintage is a leakage hazard, not a default.
    // RetrievedAtUtc is stamped here rather than caller-supplied.
    public static PriceObservation Create(
        Guid marketId,
        string externalCommodityName,
        DateOnly observedDate,
        DateTime asOfUtc,
        string source,
        int? externalCommodityId = null,
        Guid? cropId = null,
        decimal? wholesalePrice = null,
        decimal? retailPrice = null,
        decimal? minPrice = null,
        decimal? maxPrice = null,
        decimal? arrivalsKg = null,
        string? unitRaw = null,
        decimal? unitConversionFactor = null,
        bool isUnitConfirmed = false)
    {
        if (marketId == Guid.Empty)
            throw new ArgumentException("MarketId is required.", nameof(marketId));
        if (string.IsNullOrWhiteSpace(externalCommodityName))
            throw new ArgumentException("ExternalCommodityName is required.", nameof(externalCommodityName));
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source is required.", nameof(source));
        if (asOfUtc == default)
            throw new ArgumentException(
                "AsOfUtc (bulletin vintage) is required and must not be default(DateTime); " +
                "a zero vintage would make this observation eligible in every as-of window (look-ahead leakage).",
                nameof(asOfUtc));

        return new PriceObservation
        {
            Id = Guid.NewGuid(),
            MarketId = marketId,
            ExternalCommodityName = externalCommodityName,
            ObservedDate = observedDate,
            AsOfUtc = asOfUtc,
            Source = source,
            ExternalCommodityId = externalCommodityId,
            CropId = cropId,
            WholesalePrice = wholesalePrice,
            RetailPrice = retailPrice,
            MinPrice = minPrice,
            MaxPrice = maxPrice,
            ArrivalsKg = arrivalsKg,
            UnitRaw = unitRaw,
            UnitConversionFactor = unitConversionFactor,
            IsUnitConfirmed = isUnitConfirmed,
            RetrievedAtUtc = DateTime.UtcNow
        };
    }

    // Self-healing crop resolution: the canonical-mapping layer sets CropId once the source commodity is
    // mapped. Guid.Empty is rejected, and re-mapping an already-assigned observation needs overwrite:true.
    public void AssignCrop(Guid cropId, bool overwrite = false)
    {
        if (cropId == Guid.Empty)
            throw new ArgumentException("CropId must be a non-empty Guid.", nameof(cropId));
        if (CropId.HasValue && !overwrite)
            throw new InvalidOperationException(
                "CropId is already assigned; pass overwrite:true to deliberately re-map this observation.");
        CropId = cropId;
    }
}
