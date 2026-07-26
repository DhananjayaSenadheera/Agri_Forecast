using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Services;

// Read-only projection over the Logs-hub audit tables (ModelTrainingRuns, UserActivityLog) for the admin
// Logs page. Thin DB seam so the handlers are unit-testable; pure reads, no write-side isolation needed.
public interface ILogsReadStore
{
    // Page of training runs newest TrainedAtUtc first (Id DESC tiebreak), plus the total count. 1-based.
    Task<TrainingRunsPage> GetTrainingRunsPageAsync(
        int page, int pageSize, CancellationToken ct = default);

    // Page of user-activity events newest OccurredUtc first (Id DESC tiebreak), optionally filtered to a set
    // of event types (OR-combined). A null or empty set means no type filter, so ?type= and ?types= collapse
    // into one code path.
    Task<UserActivityPage> GetUserActivityPageAsync(
        int page, int pageSize, IReadOnlyCollection<UserActivityEventType>? types,
        CancellationToken ct = default);

    // Page of system-error rows newest OccurredUtc first (Id DESC tiebreak), plus the total count.
    Task<SystemErrorsPage> GetSystemErrorsAsync(
        int page, int pageSize, CancellationToken ct = default);
}

// One ModelTrainingRun row projected for the admin training-runs list.
public sealed record TrainingRunRow(
    string Version,
    DateTime TrainedAtUtc,
    bool Promoted,
    bool DecisionPromoted,
    string? PromotionDecision,
    string? BestMlKind,
    decimal? BestMlMae,
    string? BestBaselineKind,
    decimal? BestBaselineMae,
    int? NTrainRows,
    int? NCrops);

// One UserActivityLog row projected for the admin list. EventType stays the domain enum; the handler maps
// it to the wire string.
public sealed record UserActivityRow(
    DateTime OccurredUtc,
    UserActivityEventType EventType,
    Guid? ActorUserId,
    Guid? TargetUserId,
    string? UsernameAttempted,
    string? Details);

// One SystemErrors row projected for the admin system-errors list.
public sealed record SystemErrorRow(
    long Id,
    DateTime OccurredUtc,
    string Source,
    string ExceptionType,
    string? Message,
    string? Path,
    string? Method,
    string? TraceId,
    string? StackTrace);

public sealed record TrainingRunsPage(IReadOnlyList<TrainingRunRow> Items, int Total);

public sealed record UserActivityPage(IReadOnlyList<UserActivityRow> Items, int Total);

public sealed record SystemErrorsPage(IReadOnlyList<SystemErrorRow> Items, int Total);
