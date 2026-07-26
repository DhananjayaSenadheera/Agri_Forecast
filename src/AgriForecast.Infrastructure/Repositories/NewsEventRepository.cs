using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace AgriForecast.Infrastructure.Repositories;

public class NewsEventRepository : INewsEventRepository
{
    private readonly AgriForecastDbContext _db;

    public NewsEventRepository(AgriForecastDbContext db)
    {
        _db = db;
    }

    public async Task<NewsEvent> AddAsync(NewsEvent entity)
    {
        await _db.NewsEvents.AddAsync(entity);
        return entity;
    }

    public async Task<NewsEvent?> GetByIdAsync(Guid id)
    {
        // Tracked (no AsNoTracking) + includes so update can reconcile links and delete cascades.
        return await _db.NewsEvents
            .Include(n => n.AffectedCrops)
            .Include(n => n.AffectedMarkets)
            .FirstOrDefaultAsync(n => n.Id == id);
    }

    public async Task<IEnumerable<NewsEvent>> GetAllAsync()
    {
        return await _db.NewsEvents
            .AsNoTracking()
            .Include(n => n.AffectedCrops)
            .Include(n => n.AffectedMarkets)
            .OrderByDescending(n => n.PublishedAt)
            .ThenByDescending(n => n.CreatedAtUtc)
            .ToListAsync();
    }

    public Task DeleteAsync(NewsEvent entity)
    {
        _db.NewsEvents.Remove(entity);
        return Task.CompletedTask;
    }

    // Reconcile by DIFF, never clear-and-re-add: the entity arrives tracked with its links, and re-adding a
    // retained composite-key row would put a second instance with the same key into EF's identity map and
    // throw on a routine update that keeps a link.
    public void SetCropLinks(NewsEvent entity, IReadOnlyCollection<Guid>? cropIds)
    {
        var wanted = (cropIds ?? Array.Empty<Guid>()).Distinct().ToHashSet();
        foreach (var link in entity.AffectedCrops.Where(l => !wanted.Contains(l.CropId)).ToList())
            entity.AffectedCrops.Remove(link);
        var current = entity.AffectedCrops.Select(l => l.CropId).ToHashSet();
        foreach (var id in wanted.Where(id => !current.Contains(id)))
            entity.AffectedCrops.Add(new NewsEventCrop { NewsEventId = entity.Id, CropId = id });
    }

    public void SetMarketLinks(NewsEvent entity, IReadOnlyCollection<Guid>? marketIds)
    {
        var wanted = (marketIds ?? Array.Empty<Guid>()).Distinct().ToHashSet();
        foreach (var link in entity.AffectedMarkets.Where(l => !wanted.Contains(l.MarketId)).ToList())
            entity.AffectedMarkets.Remove(link);
        var current = entity.AffectedMarkets.Select(l => l.MarketId).ToHashSet();
        foreach (var id in wanted.Where(id => !current.Contains(id)))
            entity.AffectedMarkets.Add(new NewsEventMarket { NewsEventId = entity.Id, MarketId = id });
    }

    public async Task<bool> CropsExistAsync(IReadOnlyCollection<Guid>? cropIds)
    {
        var distinct = (cropIds ?? Array.Empty<Guid>()).Distinct().ToList();
        if (distinct.Count == 0) return true;
        var found = await _db.Crops.AsNoTracking().CountAsync(c => distinct.Contains(c.Id));
        return found == distinct.Count;
    }

    public async Task<bool> MarketsExistAsync(IReadOnlyCollection<Guid>? marketIds)
    {
        var distinct = (marketIds ?? Array.Empty<Guid>()).Distinct().ToList();
        if (distinct.Count == 0) return true;
        var found = await _db.Markets.AsNoTracking().CountAsync(m => distinct.Contains(m.Id));
        return found == distinct.Count;
    }
}
