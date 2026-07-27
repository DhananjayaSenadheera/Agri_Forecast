using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Services.ForecastAccuracyRead;

// Read-only projection over ForecastSnapshots for the admin accuracy surface. Pure EF LINQ, AsNoTracking
// — the aggregation maths, the UTC stamping and the date formatting live in the handlers. This class
// never writes: the nightly Python job owns every INSERT and the single UPDATE at maturity.
public class ForecastAccuracyReadStore : IForecastAccuracyReadStore
{
    private readonly AgriForecastDbContext _db;

    public ForecastAccuracyReadStore(AgriForecastDbContext db) => _db = db;

    public async Task<ForecastSnapshotCensus> GetCensusAsync(CancellationToken ct = default)
    {
        var q = _db.ForecastSnapshots.AsNoTracking();

        var byState = await q
            .GroupBy(s => s.MaturityState)
            .Select(g => new { State = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // Max over a nullable projection so an empty table returns null instead of throwing.
        var latest = await q.MaxAsync(s => (DateOnly?)s.SnapshotDate, ct);

        // Ordinal matching, mirroring the table's Latin1_General_BIN2 check constraint: the states are
        // lowercase by contract with Python, and a differently-cased value is a defect to be seen (it
        // would land in Total but in no bucket), not one to be quietly absorbed by a loose comparison.
        int Count(string state) =>
            byState.Where(x => string.Equals(x.State, state, StringComparison.Ordinal)).Sum(x => x.Count);

        return new ForecastSnapshotCensus(
            Total: byState.Sum(x => x.Count),
            Pending: Count(ForecastSnapshotMaturityStates.Pending),
            Matured: Count(ForecastSnapshotMaturityStates.Matured),
            ActualUnavailable: Count(ForecastSnapshotMaturityStates.ActualUnavailable),
            NotMaturable: Count(ForecastSnapshotMaturityStates.NotMaturable),
            LatestSnapshotDate: latest);
    }

    public async Task<IReadOnlyList<ForecastSnapshotScoringRow>> GetMaturedScoringRowsAsync(
        DateOnly fromSnapshotDate, CancellationToken ct = default)
    {
        // MATURED ONLY, and only inside the caller's window. The other three states carry no actual
        // price to score against, and letting one through would move a metric without ever showing up
        // as a row in the ledger.
        return await _db.ForecastSnapshots.AsNoTracking()
            .Where(s => s.MaturityState == ForecastSnapshotMaturityStates.Matured)
            .Where(s => s.SnapshotDate >= fromSnapshotDate)
            .Select(s => new ForecastSnapshotScoringRow(
                s.ActivePredictor,
                s.ModelVersion,
                s.PredictedPrice,
                s.ActualPrice,
                s.ReferencePrice,
                s.SignedError,
                s.PercentageError,
                s.WithinInterval))
            .ToListAsync(ct);
    }

    public async Task<ForecastSnapshotsPage> GetSnapshotsPageAsync(
        int page, int pageSize, Guid? cropId, string? modelVersion, bool maturedOnly,
        CancellationToken ct = default)
    {
        var q = _db.ForecastSnapshots.AsNoTracking();

        if (cropId.HasValue)
            q = q.Where(s => s.CropId == cropId.Value);

        // KNOWN AND ACCEPTED: this comparison runs under the database's case-INSENSITIVE collation, so
        // ?modelVersion=V17 matches rows stored as "v17", while the summary's in-memory grouping is
        // Ordinal and would keep "V17" and "v17" apart. The two can only disagree if the writer ever
        // varies the casing, which it does not (the version string is minted once per training run), and
        // a forgiving filter is the friendlier behaviour for a hand-typed admin query string.
        if (!string.IsNullOrWhiteSpace(modelVersion))
            q = q.Where(s => s.ModelVersion == modelVersion);

        if (maturedOnly)
            q = q.Where(s => s.MaturityState == ForecastSnapshotMaturityStates.Matured);

        var total = await q.CountAsync(ct);

        // Defence in depth against a Skip overflow even though GetForecastSnapshotsValidator owns the
        // real bounds.
        var skip = (int)Math.Clamp((long)(page - 1) * pageSize, 0L, int.MaxValue);

        // Inner join to Crops for the display fields: the FK is Restrict, so a snapshot's crop always
        // exists and the join can never drop a row. Joined BEFORE the ordering so ORDER BY / OFFSET land
        // on the outer query — ordering inside a subquery is not guaranteed to survive the join.
        var items = await q
            .Join(
                _db.Crops,
                s => s.CropId,
                c => c.Id,
                (s, c) => new { Snapshot = s, Crop = c })
            .OrderByDescending(x => x.Snapshot.SnapshotDate)
            .ThenByDescending(x => x.Snapshot.Id) // stable tiebreak so paging is deterministic
            .Skip(skip)
            .Take(pageSize)
            .Select(x => new ForecastSnapshotListRow(
                x.Snapshot.Id, x.Snapshot.CropId, x.Crop.Name, x.Crop.CropCode,
                x.Snapshot.SnapshotDate, x.Snapshot.HarvestDate, x.Snapshot.GrowthPeriodDays,
                x.Snapshot.PredictedPrice, x.Snapshot.LowerBound, x.Snapshot.UpperBound,
                x.Snapshot.ReferencePrice,
                x.Snapshot.Confidence, x.Snapshot.ActivePredictor, x.Snapshot.FallbackTier,
                x.Snapshot.ModelVersion, x.Snapshot.ReasonCode,
                x.Snapshot.MaturityState, x.Snapshot.ActualPrice, x.Snapshot.ActualObservedDate,
                x.Snapshot.SignedError, x.Snapshot.AbsoluteError, x.Snapshot.PercentageError,
                x.Snapshot.WithinInterval,
                x.Snapshot.CreatedAtUtc, x.Snapshot.MaturedAtUtc))
            .ToListAsync(ct);

        return new ForecastSnapshotsPage(items, total);
    }
}
