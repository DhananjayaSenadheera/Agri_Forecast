namespace AgriForecast.Domain.Entities;

// Join row linking a NewsEvent to a Market. Composite key (NewsEventId, MarketId), cascade-deleted with
// the parent event and Restrict on the Market side. The UI only surfaces crop links today.
public class NewsEventMarket
{
    public Guid NewsEventId { get; set; }
    public Guid MarketId { get; set; }
}
