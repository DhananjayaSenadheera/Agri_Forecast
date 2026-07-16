using AgriForecast.Domain.Entities;

namespace AgriForecast.Domain.Interfaces;

// Data access for captured news events (ADM-7). Mirrors IFestivalCalendarRepository, plus the
// link-reconciliation and existence helpers the many-to-many crop/market links need. News events
// are curated, Admin-mutated data (see NewsEventsController for the Admin-only mutation posture).
public interface INewsEventRepository
{
    Task<NewsEvent> AddAsync(NewsEvent entity);

    // Tracked load INCLUDING the crop/market link collections — used by update (to reconcile
    // links) and delete (EF cascades the join rows).
    Task<NewsEvent?> GetByIdAsync(Guid id);

    // All events, newest PublishedAt first (the admin News page renders a reverse-chronological
    // list). AsNoTracking + includes the link collections for read mapping.
    Task<IEnumerable<NewsEvent>> GetAllAsync();

    Task DeleteAsync(NewsEvent entity);

    // Reconcile the tracked entity's link collections to exactly the given id set (clear + re-add).
    // EF then deletes removed join rows and inserts new ones on CommitAsync. No-op on the parent's
    // scalar fields. Pass an empty/absent set to clear all links.
    void SetCropLinks(NewsEvent entity, IReadOnlyCollection<Guid>? cropIds);
    void SetMarketLinks(NewsEvent entity, IReadOnlyCollection<Guid>? marketIds);

    // Existence guards for the link validators — true when EVERY id resolves to a row (or the set
    // is empty/null, since links are optional). Keeps a bad link id a structured 400 rather than a
    // raw FK DbUpdateException (500).
    Task<bool> CropsExistAsync(IReadOnlyCollection<Guid>? cropIds);
    Task<bool> MarketsExistAsync(IReadOnlyCollection<Guid>? marketIds);
}
