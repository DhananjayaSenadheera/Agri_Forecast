using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Entities;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// Guards for the ForecastSnapshot entity (forecast-snapshot backbone). The load-bearing invariants are
/// that the PREDICTION columns are frozen once served — a matured row still shows the Low-confidence
/// fallback it was actually served with — and that the maturity states are one-way: pending -> matured
/// or actual_unavailable, with not_maturable terminal from creation. Style mirrors IngestionRunTests.cs.
/// </summary>
public class ForecastSnapshotTests
{
    private static readonly Guid CropId = Guid.Parse("aaaa1111-2222-3333-4444-555566667777");
    private static readonly DateOnly Snapshot = new(2026, 7, 1);
    private static readonly DateOnly Harvest = new(2026, 10, 9); // +100 days
    private static readonly DateTime CreatedUtc = new(2026, 7, 1, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime MaturedUtc = new(2026, 10, 10, 21, 0, 0, DateTimeKind.Utc);

    private static ForecastSnapshot Pending(
        decimal predicted = 200m,
        decimal lower = 150m,
        decimal upper = 260m,
        string confidence = "High",
        string activePredictor = "residual") =>
        ForecastSnapshot.CreatePending(
            CropId, Snapshot, Harvest, growthPeriodDays: 100,
            predictedPrice: predicted, lowerBound: lower, upperBound: upper,
            confidence: confidence, activePredictor: activePredictor, createdAtUtc: CreatedUtc,
            referencePrice: 180m, fallbackTier: null, modelVersion: "v17", reasonCode: "model_served");

    // CreatePending.

    [Fact]
    public void CreatePending_MintsPendingRow_CarryingEveryServedValueVerbatim()
    {
        var snap = ForecastSnapshot.CreatePending(
            CropId, Snapshot, Harvest, growthPeriodDays: 100,
            predictedPrice: 204.55m, lowerBound: 150.10m, upperBound: 260.90m,
            confidence: "Low", activePredictor: "crop_mean_fallback", createdAtUtc: CreatedUtc,
            referencePrice: 180.25m, fallbackTier: "crop_mean", modelVersion: "v17",
            reasonCode: "no_recent_price");

        snap.Id.Should().NotBe(Guid.Empty);
        snap.CropId.Should().Be(CropId);
        snap.SnapshotDate.Should().Be(Snapshot);
        snap.HarvestDate.Should().Be(Harvest);
        snap.GrowthPeriodDays.Should().Be(100);
        snap.PredictedPrice.Should().Be(204.55m);
        snap.LowerBound.Should().Be(150.10m);
        snap.UpperBound.Should().Be(260.90m);
        snap.ReferencePrice.Should().Be(180.25m);
        snap.Confidence.Should().Be("Low", "confidence is stored verbatim and is never upgraded");
        snap.ActivePredictor.Should().Be("crop_mean_fallback");
        snap.FallbackTier.Should().Be("crop_mean");
        snap.ModelVersion.Should().Be("v17");
        snap.ReasonCode.Should().Be("no_recent_price");
        snap.MaturityState.Should().Be(ForecastSnapshotMaturityStates.Pending);
        snap.CreatedAtUtc.Should().Be(CreatedUtc);
        snap.MaturedAtUtc.Should().BeNull();
    }

    [Fact]
    public void CreatePending_LeavesEveryOutcomeColumnNull()
    {
        var snap = Pending();

        snap.ActualPrice.Should().BeNull();
        snap.ActualObservedDate.Should().BeNull();
        snap.SignedError.Should().BeNull();
        snap.AbsoluteError.Should().BeNull();
        snap.PercentageError.Should().BeNull();
        snap.WithinInterval.Should().BeNull("an unresolved row has no outcome yet");
    }

    [Fact]
    public void CreatePending_AllowsSameDayHarvest()
    {
        // Defensive boundary: harvest == snapshot is degenerate but not invalid.
        var snap = ForecastSnapshot.CreatePending(
            CropId, Snapshot, Snapshot, 1, 200m, 150m, 260m, "High", "residual", CreatedUtc);

        snap.HarvestDate.Should().Be(Snapshot);
    }

    [Fact]
    public void CreatePending_Throws_WhenHarvestDatePrecedesSnapshotDate()
    {
        var act = () => ForecastSnapshot.CreatePending(
            CropId, Snapshot, Snapshot.AddDays(-1), 100, 200m, 150m, 260m, "High", "residual", CreatedUtc);

        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("harvestDate");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public void CreatePending_Throws_WhenGrowthPeriodNotPositive(int growthPeriodDays)
    {
        var act = () => ForecastSnapshot.CreatePending(
            CropId, Snapshot, Harvest, growthPeriodDays, 200m, 150m, 260m, "High", "residual", CreatedUtc);

        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("growthPeriodDays");
    }

    [Fact]
    public void CreatePending_Throws_WhenCropIdEmpty()
    {
        var act = () => ForecastSnapshot.CreatePending(
            Guid.Empty, Snapshot, Harvest, 100, 200m, 150m, 260m, "High", "residual", CreatedUtc);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("cropId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CreatePending_Throws_WhenConfidenceMissing(string? confidence)
    {
        // A snapshot with no confidence would let a caller render a fallback as if it were certain.
        var act = () => ForecastSnapshot.CreatePending(
            CropId, Snapshot, Harvest, 100, 200m, 150m, 260m, confidence!, "residual", CreatedUtc);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("confidence");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CreatePending_Throws_WhenActivePredictorMissing(string? activePredictor)
    {
        var act = () => ForecastSnapshot.CreatePending(
            CropId, Snapshot, Harvest, 100, 200m, 150m, 260m, "High", activePredictor!, CreatedUtc);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("activePredictor");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreatePending_Throws_WhenPredictedPriceNotPositive(decimal predicted)
    {
        var act = () => ForecastSnapshot.CreatePending(
            CropId, Snapshot, Harvest, 100, predicted, 0m, 260m, "High", "residual", CreatedUtc);

        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("predictedPrice");
    }

    [Fact]
    public void CreatePending_Throws_WhenLowerBoundNegative()
    {
        var act = () => ForecastSnapshot.CreatePending(
            CropId, Snapshot, Harvest, 100, 200m, -0.01m, 260m, "High", "residual", CreatedUtc);

        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("lowerBound");
    }

    [Fact]
    public void CreatePending_AllowsZeroLowerBound_ClippedBand()
    {
        // A wide fallback band can legitimately clip at zero; only an INVERTED band is rejected.
        var snap = ForecastSnapshot.CreatePending(
            CropId, Snapshot, Harvest, 100, 200m, 0m, 500m, "Low", "crop_mean_fallback", CreatedUtc);

        snap.LowerBound.Should().Be(0m);
    }

    [Fact]
    public void CreatePending_Throws_WhenBandInverted()
    {
        var act = () => ForecastSnapshot.CreatePending(
            CropId, Snapshot, Harvest, 100, 200m, 260m, 150m, "High", "residual", CreatedUtc);

        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("upperBound");
    }

    [Fact]
    public void CreatePending_Throws_WhenReferencePriceNegative()
    {
        var act = () => ForecastSnapshot.CreatePending(
            CropId, Snapshot, Harvest, 100, 200m, 150m, 260m, "High", "residual", CreatedUtc,
            referencePrice: -5m);

        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("referencePrice");
    }

    // CreateNotMaturable.

    [Fact]
    public void CreateNotMaturable_IsTerminalAtCreation_WithNoHarvestDateOrGrowthPeriod()
    {
        var snap = ForecastSnapshot.CreateNotMaturable(
            CropId, Snapshot, 200m, 150m, 260m, "Low", "crop_mean_fallback", CreatedUtc,
            reasonCode: "growth_period_unresolved");

        snap.MaturityState.Should().Be(ForecastSnapshotMaturityStates.NotMaturable);
        snap.HarvestDate.Should().BeNull("there is no resolvable growth period to project a harvest from");
        snap.GrowthPeriodDays.Should().BeNull();
        snap.MaturedAtUtc.Should().BeNull();
        snap.ReasonCode.Should().Be("growth_period_unresolved");
    }

    [Fact]
    public void CreateNotMaturable_Throws_WhenCropIdEmpty()
    {
        var act = () => ForecastSnapshot.CreateNotMaturable(
            Guid.Empty, Snapshot, 200m, 150m, 260m, "Low", "crop_mean_fallback", CreatedUtc);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("cropId");
    }

    [Theory]
    [InlineData("", "residual", 200, 150, 260, "confidence")]
    [InlineData("   ", "residual", 200, 150, 260, "confidence")]
    [InlineData("High", "", 200, 150, 260, "activePredictor")]
    [InlineData("High", "   ", 200, 150, 260, "activePredictor")]
    [InlineData("High", "residual", 0, 0, 260, "predictedPrice")]
    [InlineData("High", "residual", -1, 0, 260, "predictedPrice")]
    [InlineData("High", "residual", 200, -0.01, 260, "lowerBound")]
    [InlineData("High", "residual", 200, 260, 150, "upperBound")]
    public void CreateNotMaturable_AppliesTheSameArgumentGuardsAsCreatePending(
        string confidence, string activePredictor, decimal predicted, decimal lower, decimal upper,
        string expectedParam)
    {
        // A not_maturable row is terminal on arrival, so a bad value in it can never be corrected later —
        // it must be rejected by exactly the same guards as a pending row.
        var act = () => ForecastSnapshot.CreateNotMaturable(
            CropId, Snapshot, predicted, lower, upper, confidence, activePredictor, CreatedUtc);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be(expectedParam);
    }

    [Fact]
    public void CreateNotMaturable_Throws_WhenReferencePriceNegative()
    {
        var act = () => ForecastSnapshot.CreateNotMaturable(
            CropId, Snapshot, 200m, 150m, 260m, "Low", "crop_mean_fallback", CreatedUtc,
            referencePrice: -5m);

        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("referencePrice");
    }

    // Mature.

    [Fact]
    public void Mature_FillsOutcomeColumns_AndComputesSignedAndAbsoluteError()
    {
        var snap = Pending(predicted: 200m, lower: 150m, upper: 260m);
        var observed = new DateOnly(2026, 10, 8);

        snap.Mature(actualPrice: 180m, actualObservedDate: observed, maturedAtUtc: MaturedUtc);

        snap.MaturityState.Should().Be(ForecastSnapshotMaturityStates.Matured);
        snap.ActualPrice.Should().Be(180m);
        snap.ActualObservedDate.Should().Be(observed, "the trading day used must stay auditable");
        snap.SignedError.Should().Be(20m, "signed error is predicted - actual");
        snap.AbsoluteError.Should().Be(20m);
        snap.MaturedAtUtc.Should().Be(MaturedUtc);
    }

    [Fact]
    public void Mature_SignedError_IsNegative_WhenTheForecastUndershot()
    {
        var snap = Pending(predicted: 200m);

        snap.Mature(250m, Harvest, MaturedUtc);

        snap.SignedError.Should().Be(-50m);
        snap.AbsoluteError.Should().Be(50m);
    }

    [Fact]
    public void Mature_PercentageError_IsSignedPercentOfActual()
    {
        // Matches train/evaluate.regression_metrics: (pred - actual) / |actual| * 100, percent units.
        var snap = Pending(predicted: 200m);

        snap.Mature(160m, Harvest, MaturedUtc);

        snap.PercentageError.Should().BeApproximately(25m, 0.0001m);
    }

    [Theory]
    [InlineData(150, true)]   // exactly the lower bound
    [InlineData(180, true)]
    [InlineData(260, true)]   // exactly the upper bound
    [InlineData(149.99, false)]
    [InlineData(260.01, false)]
    public void Mature_WithinInterval_IsInclusiveOfBothBounds(decimal actual, bool expected)
    {
        var snap = Pending(predicted: 200m, lower: 150m, upper: 260m);

        snap.Mature(actual, Harvest, MaturedUtc);

        snap.WithinInterval.Should().Be(expected);
    }

    [Fact]
    public void Mature_NeverRewritesAPredictionColumn()
    {
        // The frozen-prediction rule: a matured row must still show what was actually served, including
        // a Low-confidence fallback. Re-scoring the record would launder the model's history.
        var snap = Pending(predicted: 200m, lower: 150m, upper: 260m, confidence: "Low",
            activePredictor: "crop_mean_fallback");

        snap.Mature(180m, Harvest, MaturedUtc);

        snap.PredictedPrice.Should().Be(200m);
        snap.LowerBound.Should().Be(150m);
        snap.UpperBound.Should().Be(260m);
        snap.ReferencePrice.Should().Be(180m);
        snap.Confidence.Should().Be("Low");
        snap.ActivePredictor.Should().Be("crop_mean_fallback");
        snap.ModelVersion.Should().Be("v17");
        snap.ReasonCode.Should().Be("model_served");
        snap.SnapshotDate.Should().Be(Snapshot);
        snap.HarvestDate.Should().Be(Harvest);
        snap.GrowthPeriodDays.Should().Be(100);
        snap.CreatedAtUtc.Should().Be(CreatedUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Mature_Throws_WhenActualPriceNotPositive(decimal actual)
    {
        var snap = Pending();

        var act = () => snap.Mature(actual, Harvest, MaturedUtc);

        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("actualPrice");
    }

    // The carry-back window: the actual must come from [HarvestDate - 5, HarvestDate]. 5 mirrors the ML
    // constant SNAPSHOT_MATCH_BACK_DAYS (== the feature pipeline's _FFILL_LIMIT).

    [Theory]
    [InlineData(0)]   // exactly the harvest date
    [InlineData(-1)]
    [InlineData(-5)]  // the far edge of the carry-back window
    public void Mature_Accepts_AnObservationInsideTheCarryBackWindow(int dayOffset)
    {
        var snap = Pending();
        var observed = Harvest.AddDays(dayOffset);

        snap.Mature(180m, observed, MaturedUtc);

        snap.MaturityState.Should().Be(ForecastSnapshotMaturityStates.Matured);
        snap.ActualObservedDate.Should().Be(observed);
    }

    [Fact]
    public void Mature_Throws_WhenTheObservationIsAfterTheHarvestDate()
    {
        // A post-harvest price is hindsight the forecast never had.
        var snap = Pending();

        var act = () => snap.Mature(180m, Harvest.AddDays(1), MaturedUtc);

        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("actualObservedDate");
    }

    [Theory]
    [InlineData(-6)]
    [InlineData(-30)]
    public void Mature_Throws_WhenTheObservationIsOlderThanTheCarryBackWindow(int dayOffset)
    {
        // A stale quote from a different market week must not be scored as the harvest-day price.
        var snap = Pending();

        var act = () => snap.Mature(180m, Harvest.AddDays(dayOffset), MaturedUtc);

        act.Should().Throw<ArgumentOutOfRangeException>().And.ParamName.Should().Be("actualObservedDate");
    }

    [Fact]
    public void Mature_LeavesTheRowUntouched_WhenAGuardRejectsIt()
    {
        var snap = Pending();

        var act = () => snap.Mature(-1m, Harvest, MaturedUtc);

        act.Should().Throw<ArgumentOutOfRangeException>();
        snap.MaturityState.Should().Be(ForecastSnapshotMaturityStates.Pending);
        snap.ActualPrice.Should().BeNull();
        snap.MaturedAtUtc.Should().BeNull();
    }

    // MarkActualUnavailable.

    [Fact]
    public void MarkActualUnavailable_IsTerminal_AndLeavesEveryErrorColumnNull()
    {
        // An unscored row is counted and surfaced, never faked into a scored one.
        var snap = Pending();

        snap.MarkActualUnavailable(MaturedUtc);

        snap.MaturityState.Should().Be(ForecastSnapshotMaturityStates.ActualUnavailable);
        snap.MaturedAtUtc.Should().Be(MaturedUtc, "the instant a row is given up on stays auditable");
        snap.ActualPrice.Should().BeNull();
        snap.ActualObservedDate.Should().BeNull();
        snap.SignedError.Should().BeNull();
        snap.AbsoluteError.Should().BeNull();
        snap.PercentageError.Should().BeNull();
        snap.WithinInterval.Should().BeNull();
    }

    // State-transition guards: every terminal state is one-way.

    [Fact]
    public void Mature_Throws_WhenTheRowIsAlreadyMatured()
    {
        var snap = Pending();
        snap.Mature(180m, Harvest, MaturedUtc);

        var act = () => snap.Mature(190m, Harvest, MaturedUtc.AddDays(1));

        act.Should().Throw<InvalidOperationException>();
        snap.ActualPrice.Should().Be(180m, "a matured row is frozen and is never re-scored");
    }

    [Fact]
    public void Mature_Throws_WhenTheRowWasMarkedActualUnavailable()
    {
        var snap = Pending();
        snap.MarkActualUnavailable(MaturedUtc);

        var act = () => snap.Mature(180m, Harvest, MaturedUtc.AddDays(1));

        act.Should().Throw<InvalidOperationException>();
        snap.MaturityState.Should().Be(ForecastSnapshotMaturityStates.ActualUnavailable);
    }

    [Fact]
    public void Mature_Throws_WhenTheRowIsNotMaturable()
    {
        var snap = ForecastSnapshot.CreateNotMaturable(
            CropId, Snapshot, 200m, 150m, 260m, "Low", "crop_mean_fallback", CreatedUtc);

        var act = () => snap.Mature(180m, Harvest, MaturedUtc);

        act.Should().Throw<InvalidOperationException>();
        snap.MaturityState.Should().Be(ForecastSnapshotMaturityStates.NotMaturable,
            "not_maturable is terminal from creation");
    }

    [Fact]
    public void MarkActualUnavailable_Throws_WhenTheRowIsAlreadyMatured()
    {
        var snap = Pending();
        snap.Mature(180m, Harvest, MaturedUtc);

        var act = () => snap.MarkActualUnavailable(MaturedUtc.AddDays(1));

        act.Should().Throw<InvalidOperationException>();
        snap.MaturityState.Should().Be(ForecastSnapshotMaturityStates.Matured);
    }

    [Fact]
    public void MarkActualUnavailable_Throws_WhenTheRowWasAlreadyMarkedUnavailable()
    {
        // Double give-up: the second sweep must not re-stamp MaturedAtUtc and move the audit trail.
        var snap = Pending();
        snap.MarkActualUnavailable(MaturedUtc);

        var act = () => snap.MarkActualUnavailable(MaturedUtc.AddDays(1));

        act.Should().Throw<InvalidOperationException>();
        snap.MaturedAtUtc.Should().Be(MaturedUtc, "the first give-up instant is the one that stands");
    }

    [Fact]
    public void MarkActualUnavailable_Throws_WhenTheRowIsNotMaturable()
    {
        var snap = ForecastSnapshot.CreateNotMaturable(
            CropId, Snapshot, 200m, 150m, 260m, "Low", "crop_mean_fallback", CreatedUtc);

        var act = () => snap.MarkActualUnavailable(MaturedUtc);

        act.Should().Throw<InvalidOperationException>();
    }

    // Stored-string pinning: these literals are the storage+wire contract shared with the Python job,
    // and the pending one is baked into the filtered index. A rename here silently orphans rows.

    [Theory]
    [InlineData("pending")]
    [InlineData("matured")]
    [InlineData("actual_unavailable")]
    [InlineData("not_maturable")]
    public void MaturityStates_ExactLowercaseSpellings_ArePinned(string expected)
    {
        ForecastSnapshotMaturityStates.All.Should().Contain(expected);
    }

    [Fact]
    public void MaturityStates_AllContainsExactlyTheFourKnownValues()
    {
        ForecastSnapshotMaturityStates.All.Should().HaveCount(4);
    }
}
