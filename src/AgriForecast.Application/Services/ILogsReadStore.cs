using AgriForecast.Domain.Enums;

namespace AgriForecast.Application.Services;

// Read-only projection over the Logs-hub audit tables (ModelTrainingRuns, UserActivityLog) for the
// admin Logs page (AdminLogsController). Thin DB seam so the GetTrainingRuns / GetUserActivity
// handlers are unit-testable with canned rows (mirrors IIngestionReadStore). Pure reads on the
// normal request-scoped, AsNoTracking DbContext — no write-side isolation needed.
public interface ILogsReadStore
{
    // Page of model-training runs newest TrainedAtUtc first (Id DESC tiebreak), plus the total count
    // (for the pager). page is 1-based; the store applies Skip/Take.
    Task<TrainingRunsPage> GetTrainingRunsPageAsync(
        int page, int pageSize, CancellationToken ct = default);

    // Page of user-activity events newest OccurredUtc first (Id DESC tiebreak), optionally filtered to
    // a SET of EventTypes (OR-combined), plus the total count of matching rows. page is 1-based.
    // A null/empty set means NO type filter — the handler collapses both ?type= (one member) and
    // ?types= (many) into this single parameter, so there is exactly one filter code path.
    Task<UserActivityPage> GetUserActivityPageAsync(
        int page, int pageSize, IReadOnlyCollection<UserActivityEventType>? types,
        CancellationToken ct = default);

    // Page of system-error rows newest OccurredUtc first (Id DESC tiebreak), plus the total count. page
    // is 1-based; the store applies Skip/Take.
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

// One UserActivityLog row projected for the admin user-activity list. EventType stays the domain enum;
// the handler maps it to the lowercase wire string.
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
