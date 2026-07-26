using System.Text.RegularExpressions;
using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// One row per ingestion source per pass; every source row of a pass shares the BatchId.
// The Worker inserts a Running row in its own commit before the source runs (so a crash leaves a null
// FinishedUtc breadcrumb), then transitions the same tracked row to Succeeded, Failed or Skipped.
// Source keys match IngestionWatermark.Source so the two line up 1:1.
public class IngestionRun
{
    public Guid Id { get; private set; }

    // Groups every source row of one daily pass.
    public Guid BatchId { get; private set; }

    // Source key, e.g. "DAMBULLA_DEC". Matches IngestionWatermark.Source and PriceObservation.Source.
    public string Source { get; private set; } = string.Empty;

    public DateTime StartedUtc { get; private set; }

    // Null while running / if the process crashed before finalizing the row.
    public DateTime? FinishedUtc { get; private set; }

    public IngestionRunStatus Status { get; private set; }

    // Coverage window this run touched (date-only). Null for sources that do not report coverage.
    public DateOnly? CoveredFromDate { get; private set; }
    public DateOnly? CoveredToDate { get; private set; }

    // Per-source counts. All nullable so an un-migrated source can write a status-only row.
    public int? RowsFetched { get; private set; }
    public int? RowsInserted { get; private set; }
    public int? RowsSkipped { get; private set; }
    public int? DistinctCrops { get; private set; }

    // Sanitized failure note only: exception type plus message (or a caller-supplied reason), with
    // path-like tokens redacted and capped to the 1000-char column. Stack traces are never stored.
    public string? ErrorSummary { get; private set; }

    // Record-keeping only; never a feature.
    public DateTime CreatedAtUtc { get; private set; }

    // nvarchar(1000) column cap shared by ErrorSummary construction.
    private const int ErrorSummaryMaxLength = 1000;

    // Redacts filesystem-path-like tokens so a server path embedded in a message is not stored.
    // Deliberately narrow (named roots only) so it never mangles URLs or routes like "/admin/ingest-harti".
    private static readonly Regex PathLikeToken = new(
        @"[A-Za-z]:\\[^\s]*" +
        @"|\\\\[^\s]+" +
        @"|/(?:Users|home|var|etc|tmp|opt|usr|private|root|mnt|srv)/[^\s]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private IngestionRun() { }

    // startedUtc is passed in rather than read from the clock so tests are deterministic.
    public static IngestionRun StartRunning(Guid batchId, string source, DateTime startedUtc)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source is required.", nameof(source));

        return new IngestionRun
        {
            Id = Guid.NewGuid(),
            BatchId = batchId,
            Source = source,
            StartedUtc = startedUtc,
            FinishedUtc = null,
            Status = IngestionRunStatus.Running,
            CreatedAtUtc = startedUtc
        };
    }

    // Counts are all optional: a status-only source passes them null.
    public void MarkSucceeded(
        DateTime finishedUtc,
        DateOnly? coveredFrom = null,
        DateOnly? coveredTo = null,
        int? rowsFetched = null,
        int? rowsInserted = null,
        int? rowsSkipped = null,
        int? distinctCrops = null)
    {
        Status = IngestionRunStatus.Succeeded;
        FinishedUtc = finishedUtc;
        CoveredFromDate = coveredFrom;
        CoveredToDate = coveredTo;
        RowsFetched = rowsFetched;
        RowsInserted = rowsInserted;
        RowsSkipped = rowsSkipped;
        DistinctCrops = distinctCrops;
    }

    // Terminal SKIP: a deliberate no-op this pass (e.g. a disabled source). Not a failure.
    public void MarkSkipped(DateTime finishedUtc)
    {
        Status = IngestionRunStatus.Skipped;
        FinishedUtc = finishedUtc;
    }

    // Terminal failure from an exception. Only the type name and message are used, never the stack trace.
    public void MarkFailed(DateTime finishedUtc, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        MarkFailedCore(finishedUtc, $"{ex.GetType().Name}: {ex.Message}");
    }

    // Terminal failure from a caller-supplied reason, for a fail-safe source that never throws to the
    // Worker. The reason is sanitized exactly like an exception message.
    public void MarkFailed(DateTime finishedUtc, string reason)
    {
        MarkFailedCore(finishedUtc, reason ?? string.Empty);
    }

    private void MarkFailedCore(DateTime finishedUtc, string rawSummary)
    {
        Status = IngestionRunStatus.Failed;
        FinishedUtc = finishedUtc;
        ErrorSummary = Sanitize(rawSummary);
    }

    // Redact path-like tokens, then hard-cap to the column length.
    private static string Sanitize(string raw)
    {
        var redacted = PathLikeToken.Replace(raw ?? string.Empty, "<path>");
        return redacted.Length > ErrorSummaryMaxLength
            ? redacted[..ErrorSummaryMaxLength]
            : redacted;
    }
}
