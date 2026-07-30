using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Write-side access to PlantedDateRemovals. INSERT ONLY, on purpose: the table is append-only, so there is
// no update, no delete and no by-id load a caller could reach for.
//
// The insert is enrolled in the CALLER's unit of work — it shares the request's DbContext and is committed by
// IUnitofWorkRepository.CommitAsync, never by a SaveChanges of its own. That is what puts the removal row
// and the cleared date in ONE transaction; an audit-style writer with its own scope (see IUserActivityAudit)
// would be the wrong seam here, because a lost row would leave a date cleared for no recorded reason.
public interface IPlantedDateRemovalRepository
{
    Task AddAsync(PlantedDateRemoval removal, CancellationToken ct = default);
}
