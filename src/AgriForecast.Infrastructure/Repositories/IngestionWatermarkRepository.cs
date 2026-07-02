using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

public class IngestionWatermarkRepository : IIngestionWatermarkRepository
{
    private readonly AgriForecastDbContext _db;

    public IngestionWatermarkRepository(AgriForecastDbContext db)
    {
        _db = db;
    }

    public async Task<IngestionWatermark?> GetAsync(string source, CancellationToken ct = default)
    {
        return await _db.IngestionWatermarks
            .FirstOrDefaultAsync(w => w.Source == source, ct);
    }

    public async Task<IngestionWatermark> GetOrCreateAsync(
        string source,
        IngestionSourceStatus initialStatus = IngestionSourceStatus.Ok,
        string? initialMessage = null,
        CancellationToken ct = default)
    {
        var existing = await _db.IngestionWatermarks
            .FirstOrDefaultAsync(w => w.Source == source, ct);
        if (existing is not null)
            return existing;

        var created = IngestionWatermark.Create(source, initialStatus, initialMessage);
        await _db.IngestionWatermarks.AddAsync(created, ct);
        await _db.SaveChangesAsync(ct);
        return created;
    }

    public async Task SaveChangesAsync(CancellationToken ct = default)
    {
        await _db.SaveChangesAsync(ct);
    }
}
