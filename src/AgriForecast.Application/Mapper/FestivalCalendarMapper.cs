using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using AgriForecast.Domain.Entities;

namespace AgriForecast.Application.Mapper;

// Hand-written mapper for FestivalCalendarEntry (same style as PolicyFlagMapper — no AutoMapper).
public static class FestivalCalendarMapper
{
    // FestivalCalendar_CreateDto -> FestivalCalendarEntry.
    // Date normalised to date-only (leakage guard — the ML as-of-join key must never carry a
    // hidden time); new Guid Id; CreatedAtUtc = UtcNow (record-keeping only, never a feature).
    public static FestivalCalendarEntry ToEntity(this FestivalCalendar_CreateDto src)
    {
        return new FestivalCalendarEntry
        {
            Id = Guid.NewGuid(),
            FestivalKey = src.FestivalKey,
            Date = src.Date.Date,
            LeadUpDays = src.LeadUpDays,
            IsProvisional = src.IsProvisional,
            Source = src.Source,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    // FestivalCalendar_UpdateDto -> apply onto the tracked entity. Date normalised to date-only.
    // CreatedAtUtc is preserved (audit of first insert, not touched on edit).
    public static FestivalCalendarEntry ApplyTo(this FestivalCalendar_UpdateDto src, FestivalCalendarEntry target)
    {
        target.FestivalKey = src.FestivalKey;
        target.Date = src.Date.Date;
        target.LeadUpDays = src.LeadUpDays;
        target.IsProvisional = src.IsProvisional;
        target.Source = src.Source;
        return target;
    }

    // FestivalCalendarEntry -> FestivalCalendar_GetDto (convention-only, all same-name members).
    public static FestivalCalendar_GetDto ToGetDto(this FestivalCalendarEntry src)
    {
        return new FestivalCalendar_GetDto
        {
            Id = src.Id,
            FestivalKey = src.FestivalKey,
            Date = src.Date,
            LeadUpDays = src.LeadUpDays,
            IsProvisional = src.IsProvisional,
            Source = src.Source,
            CreatedAtUtc = src.CreatedAtUtc
        };
    }

    public static List<FestivalCalendar_GetDto> ToGetDtoList(this IEnumerable<FestivalCalendarEntry> src)
    {
        return src.Select(f => f.ToGetDto()).ToList();
    }
}
