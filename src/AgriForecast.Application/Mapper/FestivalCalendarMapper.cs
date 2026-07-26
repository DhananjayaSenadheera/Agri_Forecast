using AgriForecast.Application.Requests.FestivalCalendar.DTOs;
using AgriForecast.Domain.Entities;

namespace AgriForecast.Application.Mapper;

// Hand-written mapper for FestivalCalendarEntry (the house style — no AutoMapper).
public static class FestivalCalendarMapper
{
    // Date is normalised to date-only: it is the ML as-of-join key and must never carry a time.
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

    // In-place update. Date is normalised to date-only; CreatedAtUtc keeps its first-insert value.
    public static FestivalCalendarEntry ApplyTo(this FestivalCalendar_UpdateDto src, FestivalCalendarEntry target)
    {
        target.FestivalKey = src.FestivalKey;
        target.Date = src.Date.Date;
        target.LeadUpDays = src.LeadUpDays;
        target.IsProvisional = src.IsProvisional;
        target.Source = src.Source;
        return target;
    }

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
