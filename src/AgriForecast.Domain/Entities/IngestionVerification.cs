using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// One row per post-pass data-quality verification. Rows are written by the Python side; .NET owns the
// schema and reads them. PipelineDate is date-only; ChecksJson is guarded by an ISJSON check constraint
// so a malformed blob can never land.
public class IngestionVerification
{
    public Guid Id { get; private set; }

    // Links to the pass (IngestionRun.BatchId). Null for an ad-hoc verification not tied to a pass.
    public Guid? BatchId { get; private set; }

    // The pipeline/business date the checks describe (date-only, no hidden time).
    public DateOnly PipelineDate { get; private set; }

    // When the verification actually ran (full instant).
    public DateTime RunUtc { get; private set; }

    public IngestionVerificationStatus OverallStatus { get; private set; }

    public int NChecksPass { get; private set; }
    public int NChecksWarn { get; private set; }
    public int NChecksFail { get; private set; }

    // Short human-facing roll-up (audit only).
    public string? Summary { get; private set; }

    // Raw per-check detail as JSON, guarded by an ISJSON check constraint configured in the DbContext.
    public string ChecksJson { get; private set; } = string.Empty;

    public int? DurationMs { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    private IngestionVerification() { }

    // For tests and future .NET reads. createdAtUtc is passed in so tests are deterministic.
    public static IngestionVerification Create(
        Guid? batchId,
        DateOnly pipelineDate,
        DateTime runUtc,
        IngestionVerificationStatus overallStatus,
        int nChecksPass,
        int nChecksWarn,
        int nChecksFail,
        string checksJson,
        DateTime createdAtUtc,
        string? summary = null,
        int? durationMs = null)
    {
        if (string.IsNullOrWhiteSpace(checksJson))
            throw new ArgumentException("ChecksJson is required (NOT NULL, ISJSON-checked).", nameof(checksJson));

        return new IngestionVerification
        {
            Id = Guid.NewGuid(),
            BatchId = batchId,
            PipelineDate = pipelineDate,
            RunUtc = runUtc,
            OverallStatus = overallStatus,
            NChecksPass = nChecksPass,
            NChecksWarn = nChecksWarn,
            NChecksFail = nChecksFail,
            ChecksJson = checksJson,
            Summary = summary,
            DurationMs = durationMs,
            CreatedAtUtc = createdAtUtc
        };
    }
}
