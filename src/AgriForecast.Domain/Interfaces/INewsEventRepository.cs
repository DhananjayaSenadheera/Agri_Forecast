using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Data access for captured news events, plus the helpers the many-to-many crop/market links need.
public interface INewsEventRepository
{
    Task<NewsEvent> AddAsync(NewsEvent entity);

    // Tracked load including the link collections: update reconciles them, delete cascades the join rows.
    Task<NewsEvent?> GetByIdAsync(Guid id);

    // Newest PublishedAt first, AsNoTracking, with the link collections included.
    Task<IEnumerable<NewsEvent>> GetAllAsync();

    Task DeleteAsync(NewsEvent entity);

    // Reconciles the tracked entity's links to exactly the given id set; pass null or empty to clear.
    void SetCropLinks(NewsEvent entity, IReadOnlyCollection<Guid>? cropIds);
    void SetMarketLinks(NewsEvent entity, IReadOnlyCollection<Guid>? marketIds);

    // True when every id resolves to a row, or the set is empty since links are optional. Keeps a bad
    // link id a structured 400 rather than a raw FK error.
    Task<bool> CropsExistAsync(IReadOnlyCollection<Guid>? cropIds);
    Task<bool> MarketsExistAsync(IReadOnlyCollection<Guid>? marketIds);
}
