using AgriForecast.Application.Requests.NewsEvents.DTOs;
using AgriForecast.Domain.Entities;

namespace AgriForecast.Application.Mapper;

// Hand-written mapper for NewsEvent (same style as FestivalCalendarMapper / PolicyFlagMapper — no
// AutoMapper). Crop/market link rows are built here on create; on update the links are reconciled
// by the repository (SetCropLinks/SetMarketLinks), not here.
public static class NewsEventMapper
{
    // NewsEvent_CreateDto -> NewsEvent. PublishedAt normalised to date-only (vintage leakage guard);
    // new Guid Id; CreatedAtUtc = UtcNow (record-keeping only, never a feature).
    public static NewsEvent ToEntity(this NewsEvent_CreateDto src)
    {
        var entity = new NewsEvent
        {
            Id = Guid.NewGuid(),
            EventType = src.EventType,
            Direction = src.Direction,
            Title = src.Title,
            Description = src.Description,
            PublishedAt = src.PublishedAt.Date,
            SourceUrl = src.SourceUrl,
            CreatedAtUtc = DateTime.UtcNow
        };

        foreach (var cropId in (src.AffectedCropIds ?? new List<Guid>()).Distinct())
            entity.AffectedCrops.Add(new NewsEventCrop { NewsEventId = entity.Id, CropId = cropId });

        foreach (var marketId in (src.AffectedMarketIds ?? new List<Guid>()).Distinct())
            entity.AffectedMarkets.Add(new NewsEventMarket { NewsEventId = entity.Id, MarketId = marketId });

        return entity;
    }

    // NewsEvent_UpdateDto -> apply onto the tracked entity. PublishedAt is DELIBERATELY NOT touched
    // (immutable vintage — the UpdateDto does not even carry it) and neither is CreatedAtUtc.
    // Link collections are reconciled by the repository, not here.
    public static NewsEvent ApplyTo(this NewsEvent_UpdateDto src, NewsEvent target)
    {
        target.EventType = src.EventType;
        target.Direction = src.Direction;
        target.Title = src.Title;
        target.Description = src.Description;
        target.SourceUrl = src.SourceUrl;
        return target;
    }

    // NewsEvent -> NewsEvent_GetDto. Link collections flattened to id lists.
    public static NewsEvent_GetDto ToGetDto(this NewsEvent src)
    {
        return new NewsEvent_GetDto
        {
            Id = src.Id,
            EventType = src.EventType,
            Direction = src.Direction,
            Title = src.Title,
            Description = src.Description,
            PublishedAt = src.PublishedAt,
            SourceUrl = src.SourceUrl,
            AffectedCropIds = src.AffectedCrops.Select(c => c.CropId).ToList(),
            AffectedMarketIds = src.AffectedMarkets.Select(m => m.MarketId).ToList(),
            CreatedAtUtc = src.CreatedAtUtc
        };
    }

    public static List<NewsEvent_GetDto> ToGetDtoList(this IEnumerable<NewsEvent> src)
    {
        return src.Select(n => n.ToGetDto()).ToList();
    }
}
