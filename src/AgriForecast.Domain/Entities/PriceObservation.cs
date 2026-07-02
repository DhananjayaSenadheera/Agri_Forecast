namespace AgriForecast.Domain.Entities;

// Point-in-time price observation, supersedes MarketPrice (which is kept intact for
// back-compat). One row = one commodity's price at one market for one ObservedDate,
// as published by one Source. All price fields are nullable so partial bulletins
// (e.g. wholesale-only, or arrivals-only) insert cleanly.
//
// Two vintage timestamps are kept deliberately distinct:
//   ObservedDate  — the date the price is FOR (the economic event date).
//   AsOfUtc       — when the bulletin that carried this price was published
//                   (point-in-time vintage; the ML layer as-of-joins on this to
//                   avoid look-ahead leakage — never treat ObservedDate as "known" earlier).
//   RetrievedAtUtc— record-keeping only (when our ingestion fetched it); never a feature.
//
// Setters are private and the entity is constructed via the Create factory (house style,
// as Crop/EconomicCenter/Market). EF materialises through its private-setter support.
// This is a leakage safeguard: AsOfUtc is REQUIRED and rejected if default(DateTime), so a
// forgetful ingestion path can never write 0001-01-01 — a row that would otherwise be
// "already published" in every as-of window and silently leak look-ahead information.
public class PriceObservation
{
    public Guid Id { get; private set; }

    // FK -> Markets. Required: every observation belongs to a market dimension.
    public Guid MarketId { get; private set; }

    // FK -> Crops. Nullable: self-healed later by the canonical commodity mapping,
    // exactly as MarketPrice.CropId is resolved from Crop.ExternalProductId during ingestion.
    public Guid? CropId { get; private set; }

    // Source-native commodity identity. ExternalCommodityId is nullable because some
    // sources (HARTI/CBSL bulletins) key on name only; ExternalCommodityName is always set.
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

    // Bulletin publication timestamp — the point-in-time vintage (distinct from ObservedDate).
    public DateTime AsOfUtc { get; private set; }

    // Provenance, e.g. "HARTI", "CBSL", "DEC-DAMBULLA".
    public string Source { get; private set; } = string.Empty;

    // Audit: when our ingestion fetched the row (record-keeping only; never a feature).
    public DateTime RetrievedAtUtc { get; private set; }

    private PriceObservation() { }

    // Factory for ingestion. Requires the identity + point-in-time keys so a partial
    // bulletin still lands with a valid vintage; price/arrivals/commodity-id/crop-id are
    // optional. RetrievedAtUtc is stamped here (never caller-supplied) — it is audit-only.
    // Throws if asOfUtc is default(DateTime): a missing vintage is a leakage hazard, not a
    // recoverable default.
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
        decimal? arrivalsKg = null)
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
            RetrievedAtUtc = DateTime.UtcNow
        };
    }

    // Self-healing crop resolution: the canonical-mapping layer sets CropId once the
    // source commodity is mapped. Mirrors how MarketPrice.CropId is back-filled.
    public void AssignCrop(Guid cropId)
    {
        CropId = cropId;
    }
}
