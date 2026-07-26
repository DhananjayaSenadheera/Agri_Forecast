using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.Extensions.DependencyInjection;

namespace AgriForecast.Infrastructure.Repositories;

// Every audit write runs in its OWN service scope with a fresh DbContext, so it never shares or flushes the
// ingestion pass's context: a source's half-done or failed tracked entities can never be persisted by an
// audit save, and one poisoned entity cannot cascade-fail later audit writes in the pass.
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
        // Re-attach the detached entity by its fixed Id; the row already exists, so this is an UPDATE.
        db.IngestionRuns.Update(run);
        await db.SaveChangesAsync(ct);
    }
}
