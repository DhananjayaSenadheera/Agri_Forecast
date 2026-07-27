using AgriForecast.Domain.Constants;

namespace AgriForecast.Domain.Entities;

// One frozen prediction per crop per day. The nightly Python job inserts a row at snapshot time and
// updates it once, later, when the harvest date passes and an actual price is known. .NET owns the
// schema; .NET reads (admin accuracy surface + farmer portfolio). Precedent: ModelTrainingRun.
//
// Two rules the type exists to enforce:
//  1. The PREDICTION columns are FROZEN. SnapshotDate..ReasonCode are written once at creation and no
//     method on this entity can rewrite them. A matured row is evidence of what was actually served,
//     including a Low-confidence fallback — re-scoring it later would launder the record.
//  2. MaturityState transitions are one-way: pending -> matured | actual_unavailable. not_maturable is
//     terminal from creation (no growth period, so no harvest date to score against) and both terminal
//     resolutions are final — a row is never re-matured.
//
// Snapshots are write-only serving artifacts. They are NEVER read back as ML features.
public class ForecastSnapshot
{
    public Guid Id { get; private set; }

    // FK -> Crops (Restrict). The ML side keys on the lowercase GUID string.
    public Guid CropId { get; private set; }

    // = plantDate = the pass's as-of day. Date-only, no hidden time component.
    public DateOnly SnapshotDate { get; private set; }

    // SnapshotDate + GrowthPeriodDays as served by /predict (NOT the Crop column). Null only on a
    // not_maturable row, where the growth period could not be resolved.
    public DateOnly? HarvestDate { get; private set; }
    public int? GrowthPeriodDays { get; private set; }

    // The served forecast, verbatim: p50 and the nominal 80% band (p10 / p90).
    public decimal PredictedPrice { get; private set; }
    public decimal LowerBound { get; private set; }
    public decimal UpperBound { get; private set; }

    // AvgPrice known at plantDate (the carry-forward anchor), stored so directional accuracy is
    // computable later without re-reading history. Null when no anchor was available.
    public decimal? ReferencePrice { get; private set; }

    // Low / Medium / High, verbatim as served. NEVER upgraded — a Low-confidence fallback stays Low.
    public string Confidence { get; private set; } = string.Empty;

    // e.g. "residual" / "crop_mean_fallback", plus the served fallback tier when there was one.
    public string ActivePredictor { get; private set; } = string.Empty;
    public string? FallbackTier { get; private set; }

    public string? ModelVersion { get; private set; }
    public string? ReasonCode { get; private set; }

    // One of ForecastSnapshotMaturityStates.
    public string MaturityState { get; private set; } = ForecastSnapshotMaturityStates.Pending;

    // Filled once, at maturity. ActualObservedDate audits which trading day the carry-back landed on.
    public decimal? ActualPrice { get; private set; }
    public DateOnly? ActualObservedDate { get; private set; }
    public decimal? SignedError { get; private set; }
    public decimal? AbsoluteError { get; private set; }
    public decimal? PercentageError { get; private set; }
    public bool? WithinInterval { get; private set; }

    // Record-keeping only; never a feature.
    public DateTime CreatedAtUtc { get; private set; }

    // The instant this row reached a TERMINAL state — set for matured AND for actual_unavailable, so a
    // given-up row still records when it was given up on. Null while pending and on not_maturable rows
    // (terminal at creation, so CreatedAtUtc already carries that instant).
    public DateTime? MaturedAtUtc { get; private set; }

    // Denominator clip for the percentage error, matching train/evaluate.regression_metrics so the .NET
    // and Python computations of the same column cannot drift.
    private const decimal PercentageErrorDenominatorFloor = 0.000001m;

    private ForecastSnapshot() { }

    /// <summary>
    /// A normal, maturable snapshot. Times are passed in rather than read from the clock so tests are
    /// deterministic; in production the DB default fills CreatedAtUtc when Python omits it.
    /// </summary>
    public static ForecastSnapshot CreatePending(
        Guid cropId,
        DateOnly snapshotDate,
        DateOnly harvestDate,
        int growthPeriodDays,
        decimal predictedPrice,
        decimal lowerBound,
        decimal upperBound,
        string confidence,
        string activePredictor,
        DateTime createdAtUtc,
        decimal? referencePrice = null,
        string? fallbackTier = null,
        string? modelVersion = null,
        string? reasonCode = null)
    {
        if (growthPeriodDays <= 0)
            throw new ArgumentOutOfRangeException(nameof(growthPeriodDays), "Growth period must be positive.");

        if (harvestDate < snapshotDate)
            throw new ArgumentOutOfRangeException(nameof(harvestDate), "Harvest date cannot precede the snapshot date.");

        var snapshot = CreateCore(
            cropId, snapshotDate, predictedPrice, lowerBound, upperBound, confidence, activePredictor,
            createdAtUtc, referencePrice, fallbackTier, modelVersion, reasonCode);

        snapshot.HarvestDate = harvestDate;
        snapshot.GrowthPeriodDays = growthPeriodDays;
        snapshot.MaturityState = ForecastSnapshotMaturityStates.Pending;
        return snapshot;
    }

    /// <summary>
    /// A snapshot for a crop whose growth period could not be resolved: it is still recorded (the pass
    /// never silently drops a crop) but it has no harvest date, so it is TERMINAL at creation and stays
    /// out of the accuracy aggregates.
    /// </summary>
    public static ForecastSnapshot CreateNotMaturable(
        Guid cropId,
        DateOnly snapshotDate,
        decimal predictedPrice,
        decimal lowerBound,
        decimal upperBound,
        string confidence,
        string activePredictor,
        DateTime createdAtUtc,
        decimal? referencePrice = null,
        string? fallbackTier = null,
        string? modelVersion = null,
        string? reasonCode = null)
    {
        var snapshot = CreateCore(
            cropId, snapshotDate, predictedPrice, lowerBound, upperBound, confidence, activePredictor,
            createdAtUtc, referencePrice, fallbackTier, modelVersion, reasonCode);

        snapshot.HarvestDate = null;
        snapshot.GrowthPeriodDays = null;
        snapshot.MaturityState = ForecastSnapshotMaturityStates.NotMaturable;
        return snapshot;
    }

    private static ForecastSnapshot CreateCore(
        Guid cropId,
        DateOnly snapshotDate,
        decimal predictedPrice,
        decimal lowerBound,
        decimal upperBound,
        string confidence,
        string activePredictor,
        DateTime createdAtUtc,
        decimal? referencePrice,
        string? fallbackTier,
        string? modelVersion,
        string? reasonCode)
    {
        if (cropId == Guid.Empty)
            throw new ArgumentException("CropId is required.", nameof(cropId));

        if (string.IsNullOrWhiteSpace(confidence))
            throw new ArgumentException("Confidence is required.", nameof(confidence));

        if (string.IsNullOrWhiteSpace(activePredictor))
            throw new ArgumentException("ActivePredictor is required.", nameof(activePredictor));

        if (predictedPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(predictedPrice), "Predicted price must be positive.");

        // A band may be clipped at zero, but it may never be inverted.
        if (lowerBound < 0)
            throw new ArgumentOutOfRangeException(nameof(lowerBound), "Lower bound cannot be negative.");

        if (upperBound < lowerBound)
            throw new ArgumentOutOfRangeException(nameof(upperBound), "Upper bound cannot be below the lower bound.");

        if (referencePrice is < 0)
            throw new ArgumentOutOfRangeException(nameof(referencePrice), "Reference price cannot be negative.");

        return new ForecastSnapshot
        {
            Id = Guid.NewGuid(),
            CropId = cropId,
            SnapshotDate = snapshotDate,
            PredictedPrice = predictedPrice,
            LowerBound = lowerBound,
            UpperBound = upperBound,
            ReferencePrice = referencePrice,
            Confidence = confidence,
            ActivePredictor = activePredictor,
            FallbackTier = fallbackTier,
            ModelVersion = modelVersion,
            ReasonCode = reasonCode,
            CreatedAtUtc = createdAtUtc
        };
    }

    /// <summary>
    /// Resolves a pending row against the actual price observed on <paramref name="actualObservedDate"/>
    /// (the trading day the carry-back landed on). Fills the error columns and MaturedAtUtc only — every
    /// prediction column is left exactly as served.
    /// </summary>
    public void Mature(decimal actualPrice, DateOnly actualObservedDate, DateTime maturedAtUtc)
    {
        EnsurePending(nameof(Mature));

        if (actualPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(actualPrice), "Actual price must be positive.");

        if (actualObservedDate > DateOnly.FromDateTime(maturedAtUtc))
            throw new ArgumentOutOfRangeException(nameof(actualObservedDate), "Actual observation cannot be in the future.");

        var signed = PredictedPrice - actualPrice;

        ActualPrice = actualPrice;
        ActualObservedDate = actualObservedDate;
        SignedError = signed;
        AbsoluteError = Math.Abs(signed);

        // Signed, in PERCENT units, with the same 1e-6 denominator clip as regression_metrics.
        PercentageError = signed / Math.Max(Math.Abs(actualPrice), PercentageErrorDenominatorFloor) * 100m;

        WithinInterval = actualPrice >= LowerBound && actualPrice <= UpperBound;
        MaturityState = ForecastSnapshotMaturityStates.Matured;
        MaturedAtUtc = maturedAtUtc;
    }

    /// <summary>
    /// Terminal give-up: the harvest date passed and no actual price could be matched inside the allowed
    /// carry-back window. The error columns stay NULL — an unscored row is never faked into a scored one.
    /// </summary>
    public void MarkActualUnavailable(DateTime resolvedAtUtc)
    {
        EnsurePending(nameof(MarkActualUnavailable));

        MaturityState = ForecastSnapshotMaturityStates.ActualUnavailable;
        MaturedAtUtc = resolvedAtUtc;
    }

    private void EnsurePending(string operation)
    {
        if (MaturityState != ForecastSnapshotMaturityStates.Pending)
            throw new InvalidOperationException(
                $"{operation} requires a pending snapshot; this row is already '{MaturityState}'. " +
                "Matured, actual_unavailable and not_maturable are terminal — rows are never re-scored.");
    }
}
