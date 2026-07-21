using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Store for ingestion RUN rows (one per source per pass). CRITICAL ISOLATION CONTRACT: run-tracking
// writes MUST NOT share the ingestion pass's scoped DbContext/ChangeTracker — otherwise an audit
// SaveChanges would flush a source's half-done or failed tracked entities (persisting a batch a
// source logged as failed), and one poisoned tracked entity would cascade-fail every later audit
// write in the pass. The implementation therefore commits each write on its OWN short-lived context.
//
// Two-phase lifecycle: AddAsync inserts the Running row (own commit, so a crash leaves the row as a
// null-FinishedUtc breadcrumb); the SAME in-memory entity is then transitioned and persisted via
// UpdateAsync (own commit) — the entity is re-attached by its fixed Id in the fresh context.
public interface IIngestionRunRepository
{
    // Insert a freshly-minted Running row and commit it in its own isolated context.
    Task AddAsync(IngestionRun run, CancellationToken ct = default);

    // Persist a terminal state transition on an already-inserted row, re-attaching it by Id in a
    // fresh isolated context and committing.
    Task UpdateAsync(IngestionRun run, CancellationToken ct = default);
}
