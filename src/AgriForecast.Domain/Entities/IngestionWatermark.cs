using AgriForecast.Domain.Enums;

namespace AgriForecast.Domain.Entities;

// Per-source resume point, one row per ingestion source.
// LastSuccessUtc only ever advances on success, so a failed pass still resumes from the last good
// point. LastObservedDate is the newest ObservedDate landed and also only moves forwards. A Disabled
// source is skipped and is not a failure. All transitions go through RecordSuccess / RecordFailure /
// Disable so the watermark can never be poked into an inconsistent state.
public class IngestionWatermark
{
    public Guid Id { get; private set; }

    // Source key, e.g. "HARTI". Unique business key; matches PriceObservation.Source.
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

    // A source that is known-disabled from the outset is created Disabled so it is never mistaken for
    // "never ran but healthy".
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

    // The only method that moves LastSuccessUtc forward. Rejects a default(DateTime) successUtc, which
    // would make "resume after LastSuccessUtc" meaningless.
    public void RecordSuccess(DateTime successUtc, DateOnly? lastObservedDate = null, string? message = null)
    {
        if (successUtc == default)
            throw new ArgumentException(
                "successUtc must not be default(DateTime); a zero success watermark is not a valid resume point.",
                nameof(successUtc));

        LastSuccessUtc = successUtc;
        // Only move the high-water mark forwards; re-running an older slice must not walk it backwards.
        if (lastObservedDate.HasValue &&
            (!LastObservedDate.HasValue || lastObservedDate.Value > LastObservedDate.Value))
        {
            LastObservedDate = lastObservedDate.Value;
        }
        Status = IngestionSourceStatus.Ok;
        LastMessage = message;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    // Deliberately does not touch LastSuccessUtc / LastObservedDate: the resume point stays at the last
    // good value so the next pass resumes rather than restarts.
    public void RecordFailure(string? message = null)
    {
        Status = IngestionSourceStatus.Failed;
        LastMessage = message;
        UpdatedAtUtc = DateTime.UtcNow;
    }

    // A Disabled source is a no-op in the pass and is never counted as a failure. The resume point is
    // left alone so re-enabling resumes cleanly.
    public void Disable(string? reason = null)
    {
        Status = IngestionSourceStatus.Disabled;
        LastMessage = reason;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
