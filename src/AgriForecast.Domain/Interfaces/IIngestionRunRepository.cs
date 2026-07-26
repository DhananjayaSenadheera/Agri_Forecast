using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Store for ingestion run rows. Each write MUST commit on its own short-lived DbContext, never the
// ingestion pass's scoped one: sharing it would flush a source's half-done or failed tracked entities,
// and one poisoned entity would cascade-fail every later audit write in the pass.
// AddAsync inserts the Running row; the same in-memory entity is later re-attached by Id and persisted
// by UpdateAsync.
public interface IIngestionRunRepository
{
    Task AddAsync(IngestionRun run, CancellationToken ct = default);

    Task UpdateAsync(IngestionRun run, CancellationToken ct = default);
}
