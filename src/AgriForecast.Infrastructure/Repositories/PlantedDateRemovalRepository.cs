using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;

namespace AgriForecast.Infrastructure.Repositories;

// Append-only writer for PlantedDateRemovals. It takes the request's SCOPED DbContext, so the row it adds is
// flushed by the handler's single IUnitofWorkRepository.CommitAsync together with the cleared planting date —
// deliberately unlike UserActivityAudit, which writes in its own scope precisely so it CANNOT join the
// caller's transaction.
public class PlantedDateRemovalRepository : IPlantedDateRemovalRepository
{
    private readonly AgriForecastDbContext _db;

    public PlantedDateRemovalRepository(AgriForecastDbContext db) => _db = db;

    public async Task AddAsync(PlantedDateRemoval removal, CancellationToken ct = default)
        => await _db.PlantedDateRemovals.AddAsync(removal, ct);
}
