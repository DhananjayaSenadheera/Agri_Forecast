using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Services.IngestionRead;

// Read-only projection over the ingestion audit tables for the admin ingestion page. Pure EF LINQ,
// AsNoTracking — no business logic here (state derivation, batch-outcome roll-up, staleness window,
// and validation live in the handlers). Uses the normal request-scoped DbContext: these are reads,
// so none of the write-side isolation (IngestionRunRepository's per-write scopes) is needed.
//
// DATE HANDLING: IngestionRun.CoveredFrom/ToDate, IngestionVerification.PipelineDate and
// IngestionWatermark.LastObservedDate are all mapped to SQL "date" and modelled as DateOnly, so they
// project straight through (no midnight-DateTime coalescing like the EconomicIndicator store needs).
public class IngestionReadStore : IIngestionReadStore
{
    private readonly AgriForecastDbContext _db;

    public IngestionReadStore(AgriForecastDbContext db) => _db = db;

    // Applies the caller-supplied source exclusion (null/empty = no exclusion). The POLICY of WHICH
    // sources are excluded is the handler's (IngestionSources.ExcludedFromServiceState) — this store
    // only applies what it is given, so /runs can keep listing rows the status card ignores.
    // Materialized to a List because EF translates List.Contains to a SQL IN, and negated to
    // NOT IN (...). Source is never null (non-nullable column), so no null-handling branch is needed.
    private IQueryable<IngestionRun> RunsExcluding(IReadOnlyCollection<string>? excludeSources)
    {
        var q = _db.IngestionRuns.AsNoTracking();
        if (excludeSources is not { Count: > 0 }) return q;

        var excluded = excludeSources.ToList();
        return q.Where(r => !excluded.Contains(r.Source));
    }

    public Task<int> GetRunCountAsync(
        IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default) =>
        RunsExcluding(excludeSources).CountAsync(ct);

    public async Task<DateTime?> GetLatestUnfinishedStartedUtcAsync(
        IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default)
    {
        var q = RunsExcluding(excludeSources).Where(r => r.FinishedUtc == null);
        if (!await q.AnyAsync(ct)) return null;
        return await q.MaxAsync(r => r.StartedUtc, ct);
    }

    public Task<IngestionRunHeadRow?> GetLatestRunAsync(
        IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default) =>
        RunsExcluding(excludeSources)
            .OrderByDescending(r => r.StartedUtc)
            .ThenByDescending(r => r.Id)
            .Select(r => new IngestionRunHeadRow(r.BatchId, r.StartedUtc))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<IngestionRunStatus>> GetRunStatusesForBatchAsync(
        Guid batchId, IReadOnlyCollection<string>? excludeSources = null, CancellationToken ct = default) =>
        await RunsExcluding(excludeSources)
            .Where(r => r.BatchId == batchId)
            .Select(r => r.Status)
            .ToListAsync(ct);

    public Task<IngestionVerificationRow?> GetLatestVerificationAsync(CancellationToken ct = default) =>
        _db.IngestionVerifications.AsNoTracking()
            .OrderByDescending(v => v.RunUtc)
            .ThenByDescending(v => v.Id)
            .Select(v => new IngestionVerificationRow(
                v.BatchId, v.OverallStatus, v.RunUtc, v.PipelineDate,
                v.NChecksPass, v.NChecksWarn, v.NChecksFail, v.ChecksJson))
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<IngestionWatermarkRow>> GetWatermarksAsync(CancellationToken ct = default) =>
        await _db.IngestionWatermarks.AsNoTracking()
            .OrderBy(w => w.Source)
            .Select(w => new IngestionWatermarkRow(
                w.Source, w.Status, w.LastSuccessUtc, w.LastObservedDate, w.LastMessage))
            .ToListAsync(ct);

    public async Task<IngestionRunsPage> GetRunsPageAsync(
        int page, int pageSize, string? source, CancellationToken ct = default)
    {
        var q = _db.IngestionRuns.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(source))
            q = q.Where(r => r.Source == source);

        var total = await q.CountAsync(ct);

        // Defense-in-depth against Skip overflow/negative even though GetIngestionRunsValidator owns
        // the real bounds (page>=1, pageSize 1..100): compute the offset as long and clamp to a sane
        // non-negative int ceiling so a pathological page never throws or wraps negative.
        var skip = (int)Math.Clamp((long)(page - 1) * pageSize, 0L, int.MaxValue);

        var items = await q
            .OrderByDescending(r => r.StartedUtc)
            .ThenByDescending(r => r.Id) // stable tiebreak so paging is deterministic across pages
            .Skip(skip)
            .Take(pageSize)
            .Select(r => new IngestionRunRow(
                r.Id, r.BatchId, r.Source, r.StartedUtc, r.FinishedUtc, r.Status,
                r.CoveredFromDate, r.CoveredToDate,
                r.RowsFetched, r.RowsInserted, r.RowsSkipped, r.DistinctCrops, r.ErrorSummary))
            .ToListAsync(ct);

        return new IngestionRunsPage(items, total);
    }

    public async Task<IReadOnlyDictionary<Guid, IngestionVerificationRow>> GetLatestVerificationsByBatchAsync(
        IReadOnlyCollection<Guid> batchIds, CancellationToken ct = default)
    {
        if (batchIds.Count == 0)
            return new Dictionary<Guid, IngestionVerificationRow>();

        var idList = batchIds.Distinct().ToList();

        var rows = await _db.IngestionVerifications.AsNoTracking()
            .Where(v => v.BatchId.HasValue && idList.Contains(v.BatchId.Value))
            .Select(v => new IngestionVerificationRow(
                v.BatchId, v.OverallStatus, v.RunUtc, v.PipelineDate,
                v.NChecksPass, v.NChecksWarn, v.NChecksFail, v.ChecksJson))
            .ToListAsync(ct);

        // Latest per BatchId by RunUtc (a batch may have more than one verification row).
        return rows
            .GroupBy(v => v.BatchId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.RunUtc).First());
    }
}
