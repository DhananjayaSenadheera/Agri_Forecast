using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.Extensions.DependencyInjection;

namespace AgriForecast.Infrastructure.Repositories;

// ISOLATION (B1): every audit write runs in its OWN service scope, resolving a FRESH
// AgriForecastDbContext with an independent ChangeTracker. It therefore never shares — and never
// flushes — the ingestion pass's scoped context: a source's half-done / failed tracked entities can
// never be silently persisted by an audit save, a poisoned entity in the pass context cannot make an
// audit save throw, and one bad source cannot cascade-fail later sources' audit writes.
public class IngestionRunRepository : IIngestionRunRepository
{
    private readonly IServiceScopeFactory _scopeFactory;

    public IngestionRunRepository(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task AddAsync(IngestionRun run, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgriForecastDbContext>();
        await db.IngestionRuns.AddAsync(run, ct);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(IngestionRun run, CancellationToken ct = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AgriForecastDbContext>();
        // Re-attach the detached entity by its fixed Id and mark it modified. The row already exists
        // (AddAsync committed it), so this is an UPDATE of the terminal state.
        db.IngestionRuns.Update(run);
        await db.SaveChangesAsync(ct);
    }
}
