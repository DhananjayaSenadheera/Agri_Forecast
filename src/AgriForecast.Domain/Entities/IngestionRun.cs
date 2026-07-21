using System.Text.RegularExpressions;
using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// One row per ingestion SOURCE per pass (PR: ingestion run tracking foundation). A daily pass
// runs several sources (DAMBULLA_DEC, HARTI, WEATHER, ECONOMIC, NEWS, ...); every source gets a
// row, all sharing the pass's BatchId so the whole pass can be reconstructed.
//
// Lifecycle (driven by the Worker via IngestionRunAudit): a Running row is inserted in its OWN
// commit BEFORE the source runs (so a crash leaves a null-FinishedUtc breadcrumb), then the same
// tracked row is transitioned to Succeeded (+ optional counts) or Failed (+ sanitized error) and
// saved again. Source keys match the IngestionWatermark Source values so the two line up 1:1.
//
// Setters are private and the entity is built via StartRunning (house style, as IngestionWatermark
// / PriceObservation). All transitions go through intent-named methods so a row can never be poked
// into an inconsistent state.
public class IngestionRun
{
    public Guid Id { get; private set; }

    // Groups all sources of ONE daily pass. Indexed; the same GUID is reused across every source
    // row in a pass (read from config Ingestion:BatchId or generated once per pass by the Worker).
    public Guid BatchId { get; private set; }

    // The ingestion Source key (e.g. "DAMBULLA_DEC", "HARTI", "WEATHER"). Matches the
    // IngestionWatermark.Source / PriceObservation.Source values.
    public string Source { get; private set; } = string.Empty;

    public DateTime StartedUtc { get; private set; }

    // Null while running / if the process crashed before finalizing the row.
    public DateTime? FinishedUtc { get; private set; }

    public IngestionRunStatus Status { get; private set; }

    // Coverage window this run touched (date-only, no hidden time). Nullable — a source that does
    // not report coverage (weather/economic/news status-only rows) leaves them null.
    public DateOnly? CoveredFromDate { get; private set; }
    public DateOnly? CoveredToDate { get; private set; }

    // Per-source counts. All nullable so an un-migrated source can write a status-only row.
    public int? RowsFetched { get; private set; }
    public int? RowsInserted { get; private set; }
    public int? RowsSkipped { get; private set; }
    public int? DistinctCrops { get; private set; }

    // SANITIZED failure note only. The stack trace and ex.ToString() are NEVER consulted (only the
    // exception type name + ex.Message, or a caller-supplied reason string). Filesystem-path-like
    // tokens in the message (Windows drive paths, UNC paths, common Unix roots) are best-effort
    // redacted to "<path>" before storage, and the whole string is capped to the 1000-char column.
    public string? ErrorSummary { get; private set; }

    // Record-keeping only (row creation instant); never a feature.
    public DateTime CreatedAtUtc { get; private set; }

    // nvarchar(1000) column cap shared by ErrorSummary construction.
    private const int ErrorSummaryMaxLength = 1000;

    // Best-effort redaction of filesystem-path-like tokens so a message that embeds a server path or
    // share name does not leak it into the stored error. Covers Windows drive paths ("C:\..."), UNC
    // paths ("\\host\share"), and common Unix roots ("/Users/...", "/home/...", "/var/...", etc.).
    // Deliberately narrow (named roots, not any "/a/b") so it never mangles URLs or route names like
    // "/admin/ingest-harti". RegexOptions set for a short, bounded input.
    private static readonly Regex PathLikeToken = new(
        @"[A-Za-z]:\\[^\s]*" +
        @"|\\\\[^\s]+" +
        @"|/(?:Users|home|var|etc|tmp|opt|usr|private|root|mnt|srv)/[^\s]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private IngestionRun() { }

    // Factory. Mints a Running row for a source in a pass. startedUtc is passed in (not UtcNow
    // inside) so the Worker controls the clock and tests are deterministic — mirrors
    // IngestionWatermark.RecordSuccess(successUtc).
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

    // Terminal SUCCESS. Counts are all optional — a status-only source (weather/economic/news)
    // passes them all null and only the status + FinishedUtc move.
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

    // Terminal FAILURE from an exception. ErrorSummary = exception type name + sanitized message
    // (path-redacted, capped). The stack trace / ToString() are never consulted.
    public void MarkFailed(DateTime finishedUtc, Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        MarkFailedCore(finishedUtc, $"{ex.GetType().Name}: {ex.Message}");
    }

    // Terminal FAILURE from a caller-supplied reason string (used by a fail-safe source that never
    // throws to the Worker but still reports a failure — the same short reason its watermark gets).
    // The reason is sanitized (path-redacted, capped) exactly like an exception message.
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

    // Redact filesystem-path-like tokens, then hard-cap to the column length. The stack trace is
    // never consulted (only the message / reason), so no " at Namespace.Method() in /path:line"
    // frames leak; the path-redaction is a second belt for paths embedded in the message itself.
    private static string Sanitize(string raw)
    {
        var redacted = PathLikeToken.Replace(raw ?? string.Empty, "<path>");
        return redacted.Length > ErrorSummaryMaxLength
            ? redacted[..ErrorSummaryMaxLength]
            : redacted;
    }
}
