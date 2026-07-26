using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using AgriForecast.Domain.Entities;

namespace AgriForecast.Application.Mapper;

// Hand-written mapper for PolicyFlag (the house style — no AutoMapper).
public static class PolicyFlagMapper
{
    // EffectiveFrom/EffectiveTo are normalised to date-only (leakage guard); EffectiveTo stays nullable.
    public static PolicyFlag ToEntity(this PolicyFlag_CreateDto src)
    {
        return new PolicyFlag
        {
            Id = Guid.NewGuid(),
            PolicyType = src.PolicyType,
            Title = src.Title,
            Description = src.Description,
            EffectiveFrom = src.EffectiveFrom.Date,
            EffectiveTo = src.EffectiveTo.HasValue ? src.EffectiveTo.Value.Date : (DateTime?)null,
            Direction = src.Direction,
            Source = src.Source,
            ReferenceUrl = src.ReferenceUrl,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    // Full-object in-place replace. Id and CreatedAtUtc are preserved; the dates are normalised to
    // date-only, as on create.
    public static PolicyFlag ApplyTo(this PolicyFlag_UpdateDto src, PolicyFlag target)
    {
        target.PolicyType = src.PolicyType;
        target.Title = src.Title;
        target.Description = src.Description;
        target.EffectiveFrom = src.EffectiveFrom.Date;
        target.EffectiveTo = src.EffectiveTo.HasValue ? src.EffectiveTo.Value.Date : (DateTime?)null;
        target.Direction = src.Direction;
        target.Source = src.Source;
        target.ReferenceUrl = src.ReferenceUrl;
        return target;
    }

    public static PolicyFlag_GetDto ToGetDto(this PolicyFlag src)
    {
        return new PolicyFlag_GetDto
        {
            Id = src.Id,
            PolicyType = src.PolicyType,
            Title = src.Title,
            Description = src.Description,
            EffectiveFrom = src.EffectiveFrom,
            EffectiveTo = src.EffectiveTo,
            Direction = src.Direction,
            Source = src.Source,
            ReferenceUrl = src.ReferenceUrl,
            CreatedAtUtc = src.CreatedAtUtc
        };
    }

    public static List<PolicyFlag_GetDto> ToGetDtoList(this IEnumerable<PolicyFlag> src)
    {
        return src.Select(f => f.ToGetDto()).ToList();
    }
}
