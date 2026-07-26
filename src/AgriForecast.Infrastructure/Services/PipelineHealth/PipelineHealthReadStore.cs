using AgriForecast.Application.Services;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Services.PipelineHealth;

// Window-scoped reads over the ingestion audit tables for GET /api/admin/pipeline/health. Pure EF LINQ,
// AsNoTracking; all the state derivation lives in the handler.
public class PipelineHealthReadStore : IPipelineHealthReadStore
{
    private readonly AgriForecastDbContext _db;

    public PipelineHealthReadStore(AgriForecastDbContext db) => _db = db;

    // Two queries on purpose. The first finds which batches touched the range; the second pulls every
    // row of those batches, including rows that started before it. Without the second pass a batch that
    // began before the fire time and spilled into the range would look like it started inside it.
    public async Task<IReadOnlyList<PipelineRunRow>> GetRunsForBatchesStartedBetweenAsync(
        DateTime fromUtc, DateTime toUtcExclusive, CancellationToken ct = default)
    {
        var batchIds = await _db.IngestionRuns.AsNoTracking()
            .Where(r => r.StartedUtc >= fromUtc && r.StartedUtc < toUtcExclusive)
            .Select(r => r.BatchId)
            .Distinct()
            .ToListAsync(ct);

        if (batchIds.Count == 0)
            return Array.Empty<PipelineRunRow>();

        return await _db.IngestionRuns.AsNoTracking()
            .Where(r => batchIds.Contains(r.BatchId))
            .Select(r => new PipelineRunRow(r.BatchId, r.Source, r.StartedUtc, r.FinishedUtc, r.Status))
            .ToListAsync(ct);
    }

    public async Task<IngestionVerificationRow?> GetVerificationForBatchOrDateAsync(
        Guid? batchId, DateOnly pipelineDate, CancellationToken ct = default)
    {
        if (batchId.HasValue)
        {
            var byBatch = await Latest(_db.IngestionVerifications.AsNoTracking()
                .Where(v => v.BatchId == batchId.Value), ct);
            if (byBatch is not null)
                return byBatch;
        }

        // PipelineDate is stamped from the UTC date at verify time. The whole catch-up window sits
        // inside one UTC day (21:00 Colombo is 15:30 UTC, +6h is 21:30 UTC), so for this night's runs
        // that date always equals the Colombo date of the fire time.
        return await Latest(_db.IngestionVerifications.AsNoTracking()
            .Where(v => v.PipelineDate == pipelineDate), ct);
    }

    private static Task<IngestionVerificationRow?> Latest(
        IQueryable<Domain.Entities.IngestionVerification> query, CancellationToken ct) =>
        query
            .OrderByDescending(v => v.RunUtc)
            .ThenByDescending(v => v.Id) // stable tiebreak when two rows share an instant
            .Select(v => new IngestionVerificationRow(
                v.BatchId, v.OverallStatus, v.RunUtc, v.PipelineDate,
                v.NChecksPass, v.NChecksWarn, v.NChecksFail, v.ChecksJson))
            .FirstOrDefaultAsync(ct);
}
