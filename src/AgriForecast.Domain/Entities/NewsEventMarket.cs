namespace AgriForecast.Domain.Entities;

// Join row linking a NewsEvent to a Market it is relevant to (many-to-many). Composite key
// (NewsEventId, MarketId). Same FK behaviour as NewsEventCrop: CASCADE-deleted with its parent
// NewsEvent, RESTRICT on the Market side (a referenced Market cannot be deleted until the link is
// removed — matches the codebase's uniform Restrict dimension-FK posture).
//
// NOTE: the ForecastUI News page (ADM-7) only surfaces crop links today; market links are modelled
// here per the task's "affected crop/market links" scope so the storage side is ready when the FE
// grows a market picker — an additive, FE-safe extra.
public class NewsEventMarket
{
    public Guid NewsEventId { get; set; }
    public Guid MarketId { get; set; }
}
