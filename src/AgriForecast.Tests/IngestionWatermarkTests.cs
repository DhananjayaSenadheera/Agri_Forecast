using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// Guards for the per-source ingestion watermark (R1.1 P1 Step 6). The load-bearing invariant is
/// that a FAILED pass must NOT advance the resume point (LastSuccessUtc / LastObservedDate): if it
/// did, a source that failed mid-run would silently skip the data it never actually ingested.
/// Style mirrors MarketDomainTests.cs in this project.
/// </summary>
public class IngestionWatermarkTests
{
    // Create.

    [Fact]
    public void Create_DefaultsToOk_WithNoSuccessRecordedYet()
    {
        var wm = IngestionWatermark.Create("HARTI");

        wm.Id.Should().NotBe(Guid.Empty);
        wm.Source.Should().Be("HARTI");
        wm.Status.Should().Be(IngestionSourceStatus.Ok);
        wm.LastSuccessUtc.Should().BeNull("a freshly-created watermark has never had a successful pass");
        wm.LastObservedDate.Should().BeNull();
    }

    [Fact]
    public void Create_HonoursExplicitInitialStatus_AndMessage()
    {
        var wm = IngestionWatermark.Create("CBSL", IngestionSourceStatus.Disabled, "parser not implemented");

        wm.Status.Should().Be(IngestionSourceStatus.Disabled);
        wm.LastMessage.Should().Be("parser not implemented");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_Throws_WhenSourceIsEmptyOrWhitespace(string? source)
    {
        var act = () => IngestionWatermark.Create(source!);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("source");
    }

    // RecordSuccess.

    [Fact]
    public void RecordSuccess_AdvancesLastSuccessUtc_AndObservedDate_AndSetsOk()
    {
        var wm = IngestionWatermark.Create("HARTI");
        wm.RecordFailure("earlier failure");   // ensure status starts non-Ok
        var successAt = new DateTime(2026, 7, 2, 6, 0, 0, DateTimeKind.Utc);
        var observed = new DateOnly(2026, 7, 1);

        wm.RecordSuccess(successAt, observed, "inserted=10");

        wm.LastSuccessUtc.Should().Be(successAt);
        wm.LastObservedDate.Should().Be(observed);
        wm.Status.Should().Be(IngestionSourceStatus.Ok, "a success clears a prior failure");
        wm.LastMessage.Should().Be("inserted=10");
    }

    [Fact]
    public void RecordSuccess_Throws_WhenSuccessUtcIsDefault()
    {
        // A zero success watermark would make "resume after LastSuccessUtc" meaningless.
        var wm = IngestionWatermark.Create("HARTI");

        var act = () => wm.RecordSuccess(default);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("successUtc");
    }

    [Fact]
    public void RecordSuccess_NeverWalksObservedDateBackwards()
    {
        // Re-running an older slice must not lower the high-water mark.
        var wm = IngestionWatermark.Create("HARTI");
        wm.RecordSuccess(DateTime.UtcNow, new DateOnly(2026, 7, 1));

        wm.RecordSuccess(DateTime.UtcNow, new DateOnly(2026, 6, 1));

        wm.LastObservedDate.Should().Be(new DateOnly(2026, 7, 1),
            "an older re-run must not move the observed-date watermark backwards");
    }

    [Fact]
    public void RecordSuccess_WithNullObservedDate_KeepsPreviousHighWaterMark()
    {
        var wm = IngestionWatermark.Create("HARTI");
        wm.RecordSuccess(DateTime.UtcNow, new DateOnly(2026, 7, 1));

        wm.RecordSuccess(DateTime.UtcNow, lastObservedDate: null);

        wm.LastObservedDate.Should().Be(new DateOnly(2026, 7, 1),
            "a no-new-data success (null observed date) must not blank the watermark");
    }

    // RecordFailure.

    [Fact]
    public void RecordFailure_DoesNotTouchTheResumePoint()
    {
        // THE load-bearing invariant: a failed pass must leave LastSuccessUtc / LastObservedDate
        // exactly where the last good pass left them, so the next pass resumes rather than skips.
        var wm = IngestionWatermark.Create("HARTI");
        var goodSuccess = new DateTime(2026, 7, 1, 6, 0, 0, DateTimeKind.Utc);
        var goodObserved = new DateOnly(2026, 6, 30);
        wm.RecordSuccess(goodSuccess, goodObserved);

        wm.RecordFailure("ML service 502");

        wm.Status.Should().Be(IngestionSourceStatus.Failed);
        wm.LastSuccessUtc.Should().Be(goodSuccess, "a failure must not advance the resume point");
        wm.LastObservedDate.Should().Be(goodObserved, "a failure must not advance the observed-date watermark");
        wm.LastMessage.Should().Be("ML service 502");
    }

    // Disable.

    [Fact]
    public void Disable_SetsDisabled_AndKeepsResumePoint()
    {
        var wm = IngestionWatermark.Create("CBSL");
        var goodSuccess = new DateTime(2026, 7, 1, 6, 0, 0, DateTimeKind.Utc);
        wm.RecordSuccess(goodSuccess, new DateOnly(2026, 6, 30));

        wm.Disable("feature-flagged off");

        wm.Status.Should().Be(IngestionSourceStatus.Disabled);
        wm.LastSuccessUtc.Should().Be(goodSuccess, "disabling must not blow away the resume point");
        wm.LastMessage.Should().Be("feature-flagged off");
    }

    // Persisted-int pinning for the status enum.

    [Theory]
    [InlineData(IngestionSourceStatus.Ok, 0)]
    [InlineData(IngestionSourceStatus.Disabled, 1)]
    [InlineData(IngestionSourceStatus.Failed, 2)]
    public void IngestionSourceStatus_NumericValues_ArePinned(IngestionSourceStatus value, int expected)
    {
        // Stored as int; a reorder would silently corrupt persisted rows. If this fails, the fix
        // belongs in a migration/backfill, not in this file.
        ((int)value).Should().Be(expected);
    }
}
