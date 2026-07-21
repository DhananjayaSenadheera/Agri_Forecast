using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// One row per post-pass data-quality VERIFICATION run. WRITTEN BY THE PYTHON SIDE later — this PR
// owns the SCHEMA only (the .NET entity exists so the migration + column types + the ISJSON check
// constraint are the single source of truth for both languages). No .NET code path inserts these
// yet; the Create factory is provided for completeness / tests and future .NET reads.
//
// Discipline mirrors the reference entities: PipelineDate is date-only; RunUtc / CreatedAtUtc are
// full datetime2 audit instants (never features). ChecksJson is the raw per-check detail, guarded
// by an ISJSON check constraint so a malformed blob can never land.
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

    // Raw per-check detail as JSON. Guarded by an ISJSON([ChecksJson]) = 1 check constraint at the
    // DB level (configured in the DbContext) so a non-JSON blob can never be persisted.
    public string ChecksJson { get; private set; } = string.Empty;

    public int? DurationMs { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    private IngestionVerification() { }

    // Factory (completeness / tests / future .NET reads). Refuses empty ChecksJson — the column is
    // NOT NULL and the ISJSON check would reject an empty string at the DB anyway. createdAtUtc is
    // passed in (never an internal UtcNow) so the caller controls the clock and tests are
    // deterministic — mirrors IngestionRun.StartRunning.
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
