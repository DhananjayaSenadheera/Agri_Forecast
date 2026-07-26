using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// Guards for the IngestionRun entity (ingestion run-tracking foundation). The load-bearing
/// invariant is that a FAILED run's ErrorSummary is SANITIZED — exception type + truncated message
/// only, NEVER a stack trace or file path (frozen project rule). Style mirrors
/// IngestionWatermarkTests.cs.
/// </summary>
public class IngestionRunTests
{
    private static readonly Guid Batch = Guid.Parse("11112222-3333-4444-5555-666677778888");
    private static readonly DateTime Started = new(2026, 7, 21, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Finished = new(2026, 7, 21, 6, 5, 0, DateTimeKind.Utc);

    // StartRunning.

    [Fact]
    public void StartRunning_MintsRunningRow_WithBatchAndSource_AndNullFinished()
    {
        var run = IngestionRun.StartRunning(Batch, "DAMBULLA_DEC", Started);

        run.Id.Should().NotBe(Guid.Empty);
        run.BatchId.Should().Be(Batch);
        run.Source.Should().Be("DAMBULLA_DEC");
        run.Status.Should().Be(IngestionRunStatus.Running);
        run.StartedUtc.Should().Be(Started);
        run.FinishedUtc.Should().BeNull("a running row has not finished yet (the crash breadcrumb)");
        run.CreatedAtUtc.Should().Be(Started);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void StartRunning_Throws_WhenSourceEmptyOrWhitespace(string? source)
    {
        var act = () => IngestionRun.StartRunning(Batch, source!, Started);

        act.Should().Throw<ArgumentException>().And.ParamName.Should().Be("source");
    }

    // MarkSucceeded.

    [Fact]
    public void MarkSucceeded_WithCounts_SetsSucceeded_AndCarriesEveryCount()
    {
        var run = IngestionRun.StartRunning(Batch, "DAMBULLA_DEC", Started);

        run.MarkSucceeded(
            Finished,
            coveredFrom: new DateOnly(2026, 7, 1),
            coveredTo: new DateOnly(2026, 7, 21),
            rowsFetched: 100,
            rowsInserted: 40,
            rowsSkipped: 55,
            distinctCrops: 12);

        run.Status.Should().Be(IngestionRunStatus.Succeeded);
        run.FinishedUtc.Should().Be(Finished);
        run.CoveredFromDate.Should().Be(new DateOnly(2026, 7, 1));
        run.CoveredToDate.Should().Be(new DateOnly(2026, 7, 21));
        run.RowsFetched.Should().Be(100);
        run.RowsInserted.Should().Be(40);
        run.RowsSkipped.Should().Be(55);
        run.DistinctCrops.Should().Be(12);
        run.ErrorSummary.Should().BeNull("a success carries no error");
    }

    [Fact]
    public void MarkSucceeded_StatusOnly_LeavesCountsNull()
    {
        // A status-only source (weather/economic/news) succeeds with no counts — nulls, not zeros.
        var run = IngestionRun.StartRunning(Batch, "WEATHER", Started);

        run.MarkSucceeded(Finished);

        run.Status.Should().Be(IngestionRunStatus.Succeeded);
        run.RowsInserted.Should().BeNull();
        run.RowsSkipped.Should().BeNull();
        run.DistinctCrops.Should().BeNull();
        run.CoveredFromDate.Should().BeNull();
    }

    // MarkSkipped.

    [Fact]
    public void MarkSkipped_SetsSkipped_NotAFailure()
    {
        var run = IngestionRun.StartRunning(Batch, "CBSL", Started);

        run.MarkSkipped(Finished);

        run.Status.Should().Be(IngestionRunStatus.Skipped);
        run.FinishedUtc.Should().Be(Finished);
        run.ErrorSummary.Should().BeNull();
    }

    // MarkFailed: sanitization is the load-bearing invariant.

    [Fact]
    public void MarkFailed_SetsFailed_AndSanitizesToTypePlusMessage()
    {
        var run = IngestionRun.StartRunning(Batch, "HARTI", Started);
        var ex = new InvalidOperationException("ML service returned 502");

        run.MarkFailed(Finished, ex);

        run.Status.Should().Be(IngestionRunStatus.Failed);
        run.FinishedUtc.Should().Be(Finished);
        run.ErrorSummary.Should().Be("InvalidOperationException: ML service returned 502");
    }

    [Fact]
    public void MarkFailed_ErrorSummary_LeaksNoStackTraceOrPath()
    {
        // Throw+catch a REAL exception so it carries a populated StackTrace, then assert the stored
        // summary contains NONE of it — only the type name + message survive sanitization.
        var run = IngestionRun.StartRunning(Batch, "HARTI", Started);
        Exception caught;
        try
        {
            throw new InvalidOperationException("boom while parsing bulletin");
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        run.MarkFailed(Finished, caught);

        caught.StackTrace.Should().NotBeNullOrEmpty("the caught exception must actually have a stack trace to make this test meaningful");
        var summary = run.ErrorSummary!;
        summary.Should().Be("InvalidOperationException: boom while parsing bulletin");
        summary.Should().NotContain(" at ", "a stack-frame marker must never leak into the stored error");
        summary.Should().NotContain(".cs:", "a source path/line must never leak into the stored error");
        summary.Should().NotContain("/", "no file path separators may leak");
        summary.Should().NotContain("\\");
    }

    [Fact]
    public void MarkFailed_TruncatesToTheThousandCharColumn()
    {
        var run = IngestionRun.StartRunning(Batch, "HARTI", Started);
        var hugeMessage = new string('x', 5000);

        run.MarkFailed(Finished, new Exception(hugeMessage));

        run.ErrorSummary!.Length.Should().Be(1000, "the sanitized error must fit the nvarchar(1000) column");
    }

    // MarkFailed: path-like tokens are redacted.

    [Theory]
    [InlineData(@"Could not open C:\Users\svc\secrets\db.config for read", "C:")]
    [InlineData(@"Failed reading /Users/dhananjaya/keys/ml.pem", "/Users/")]
    [InlineData(@"Config missing at /home/agri/app/appsettings.json", "/home/")]
    [InlineData(@"Share \\fileserver\share\creds.txt unreachable", @"\\fileserver")]
    public void MarkFailed_RedactsFilesystemPathTokens(string message, string leakedFragment)
    {
        var run = IngestionRun.StartRunning(Batch, "HARTI", Started);

        run.MarkFailed(Finished, new Exception(message));

        run.ErrorSummary.Should().Contain("<path>", "a filesystem-path-like token must be redacted");
        run.ErrorSummary.Should().NotContain(leakedFragment, "the raw path token must not leak into the stored error");
    }

    [Fact]
    public void MarkFailed_DoesNotRedact_RouteNamesOrUrls()
    {
        // Narrow redaction: a route/URL path must survive (only OS filesystem roots are redacted).
        var run = IngestionRun.StartRunning(Batch, "HARTI", Started);

        run.MarkFailed(Finished, "Unparseable /admin/ingest-harti response.");

        run.ErrorSummary.Should().Be("Unparseable /admin/ingest-harti response.",
            "route names are not filesystem paths and must not be redacted");
    }

    // MarkFailed(string reason): a fail-safe source records a reason without an exception.

    [Fact]
    public void MarkFailed_WithReasonString_SetsFailed_AndStoresSanitizedReason()
    {
        var run = IngestionRun.StartRunning(Batch, "HARTI", Started);

        run.MarkFailed(Finished, "ML returned 502.");

        run.Status.Should().Be(IngestionRunStatus.Failed);
        run.FinishedUtc.Should().Be(Finished);
        run.ErrorSummary.Should().Be("ML returned 502.");
    }

    // Persisted-int pinning for both enums.

    [Theory]
    [InlineData(IngestionRunStatus.Running, 0)]
    [InlineData(IngestionRunStatus.Succeeded, 1)]
    [InlineData(IngestionRunStatus.Failed, 2)]
    [InlineData(IngestionRunStatus.Skipped, 3)]
    public void IngestionRunStatus_NumericValues_ArePinned(IngestionRunStatus value, int expected)
    {
        // Stored as int; a reorder would silently corrupt persisted rows. Fix a reorder in a
        // migration/backfill, not here.
        ((int)value).Should().Be(expected);
    }

    [Theory]
    [InlineData(IngestionVerificationStatus.Pass, 0)]
    [InlineData(IngestionVerificationStatus.Warn, 1)]
    [InlineData(IngestionVerificationStatus.Fail, 2)]
    public void IngestionVerificationStatus_NumericValues_ArePinned(IngestionVerificationStatus value, int expected)
    {
        ((int)value).Should().Be(expected);
    }
}
