using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// Per-source ingestion watermark (R1.1 P1 Step 6). One row per ingestion Source
// ("HARTI", "CBSL", ...) recording the resume point of that source so a pass can pick up
// where the last SUCCESSFUL pass left off instead of restarting from the beginning.
//
// Resumability contract:
//   LastSuccessUtc     — when this source last completed a full pass successfully. This is the
//                        watermark services read to resume; it is ONLY advanced on success
//                        (RecordSuccess). A failed pass never moves it backwards or forwards.
//   LastObservedDate   — the newest ObservedDate this source has landed data for (nullable;
//                        a source may resume by "only fetch bulletins after this date"). It is
//                        the price-vintage high-water mark, distinct from the wall-clock
//                        LastSuccessUtc. Set on success alongside LastSuccessUtc.
//   Status             — Ok / Disabled / Failed (see IngestionSourceStatus). A Disabled source
//                        is skipped and is NOT a failure; a Failed source keeps its last-good
//                        LastSuccessUtc so the next pass still resumes.
//   LastMessage        — short human-facing note for the last transition (audit only; never a
//                        feature). E.g. counts on success, or the disabled reason.
//
// Setters are private and the entity is constructed via the Create factory (house style, as
// PriceObservation/Market). All state transitions go through explicit intent-named methods
// (RecordSuccess / RecordFailure / Disable) so the watermark can never be poked into an
// inconsistent state (e.g. LastSuccessUtc advanced on a failure).
public class IngestionWatermark
{
    public Guid Id { get; private set; }

    // The ingestion Source key this watermark tracks (e.g. "HARTI", "CBSL"). Business key —
    // unique. Matches PriceObservation.Source values so the two line up 1:1.
    public string Source { get; private set; } = string.Empty;

    // Resume point: last SUCCESSFUL completion. Nullable until the first successful pass.
    public DateTime? LastSuccessUtc { get; private set; }

    // Price-vintage high-water mark: newest ObservedDate landed. Nullable.
    public DateOnly? LastObservedDate { get; private set; }

    public IngestionSourceStatus Status { get; private set; }

    // Audit-only note on the last transition (counts on success, reason on disable, etc.).
    public string? LastMessage { get; private set; }

    // Audit: last time this row was touched at all (success, failure, or disable).
    public DateTime UpdatedAtUtc { get; private set; }

    private IngestionWatermark() { }

    // Factory. A freshly-created watermark starts in Ok with no success recorded yet
    // (LastSuccessUtc null) UNLESS an explicit initial status is supplied — a source that is
    // known-disabled from the outset (e.g. CBSL) is created directly Disabled so it is never
    // mistaken for "never ran but healthy".
    public static IngestionWatermark Create(
        string source,
        IngestionSourceStatus initialStatus = IngestionSourceStatus.Ok,
        string? initialMessage = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source is required.", nameof(source));

        return new IngestionWatermark
        {
            Id = Guid.NewGuid(),
            Source = source,
            Status = initialStatus,
            LastMessage = initialMessage,
            LastSuccessUtc = null,
            LastObservedDate = null,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    // Advance the watermark on a successful pass. This is the ONLY method that moves
    // LastSuccessUtc forward. lastObservedDate is optional (a source may not know its newest
    // observed date, e.g. a trigger-only pass). Refuses a default(DateTime) successUtc — a
    // zero watermark would make "resume after LastSuccessUtc" meaningless.
    public void RecordSuccess(DateTime successUtc, DateOnly? lastObservedDate = null, string? message = null)
    {
        if (successUtc == default)
            throw new ArgumentException(
                "successUtc must not be default(DateTime); a zero success watermark is not a valid resume point.",
                nameof(successUtc));

        LastSuccessUtc = successUtc;
        // Only advance LastObservedDate forwards; a lower/older value never overwrites a newer
        // high-water mark (a re-run of an older slice must not walk the watermark backwards).
        if (lastObservedDate.HasValue &&
            (!LastObservedDate.HasValue || lastObservedDate.Value > LastObservedDate.Value))
        {
            LastObservedDate = lastObservedDate.Value;
        }
        Status = IngestionSourceStatus.Ok;
        LastMessage = message;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    // Record a failed attempt. Deliberately does NOT touch LastSuccessUtc / LastObservedDate:
    // the resume point stays at the last good value so the next pass resumes rather than
    // restarts. Only Status + LastMessage + UpdatedAtUtc move.
    public void RecordFailure(string? message = null)
    {
        Status = IngestionSourceStatus.Failed;
        LastMessage = message;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    // Switch the source OFF. A Disabled source is a no-op in the pass and is never counted as a
    // failure. Also does not touch the resume point (so re-enabling later resumes cleanly).
    public void Disable(string? reason = null)
    {
        Status = IngestionSourceStatus.Disabled;
        LastMessage = reason;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
