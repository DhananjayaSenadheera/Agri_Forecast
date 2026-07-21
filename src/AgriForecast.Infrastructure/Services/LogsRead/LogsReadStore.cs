using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Services.LogsRead;

// Read-only projection over the Logs-hub audit tables for the admin Logs page. Pure EF LINQ,
// AsNoTracking — no business logic here (validation + wire-string/UTC mapping live in the handlers).
// Uses the normal request-scoped DbContext: these are reads, so none of the write-side isolation
// (UserActivityAudit's per-write scopes) is needed. Mirrors IngestionReadStore.
public class LogsReadStore : ILogsReadStore
{
    private readonly AgriForecastDbContext _db;

    public LogsReadStore(AgriForecastDbContext db) => _db = db;

    public async Task<TrainingRunsPage> GetTrainingRunsPageAsync(
        int page, int pageSize, CancellationToken ct = default)
    {
        var q = _db.ModelTrainingRuns.AsNoTracking();

        var total = await q.CountAsync(ct);

        // Defense-in-depth against Skip overflow/negative even though GetTrainingRunsValidator owns
        // the real bounds (page>=1, pageSize 1..100): clamp the offset to a sane non-negative int.
        var skip = (int)Math.Clamp((long)(page - 1) * pageSize, 0L, int.MaxValue);

        var items = await q
            .OrderByDescending(r => r.TrainedAtUtc)
            .ThenByDescending(r => r.Id) // stable tiebreak so paging is deterministic across pages
            .Skip(skip)
            .Take(pageSize)
            .Select(r => new TrainingRunRow(
                r.Version, r.TrainedAtUtc, r.Promoted, r.DecisionPromoted, r.PromotionDecision,
                r.BestMlKind, r.BestMlMae, r.BestBaselineKind, r.BestBaselineMae,
                r.NTrainRows, r.NCrops))
            .ToListAsync(ct);

        return new TrainingRunsPage(items, total);
    }

    public async Task<UserActivityPage> GetUserActivityPageAsync(
        int page, int pageSize, UserActivityEventType? type, CancellationToken ct = default)
    {
        var q = _db.UserActivityLog.AsNoTracking();
        if (type.HasValue)
            q = q.Where(e => e.EventType == type.Value);

        var total = await q.CountAsync(ct);

        var skip = (int)Math.Clamp((long)(page - 1) * pageSize, 0L, int.MaxValue);

        var items = await q
            .OrderByDescending(e => e.OccurredUtc)
            .ThenByDescending(e => e.Id) // stable tiebreak so paging is deterministic across pages
            .Skip(skip)
            .Take(pageSize)
            .Select(e => new UserActivityRow(
                e.OccurredUtc, e.EventType, e.ActorUserId, e.TargetUserId,
                e.UsernameAttempted, e.Details))
            .ToListAsync(ct);

        return new UserActivityPage(items, total);
    }
}
