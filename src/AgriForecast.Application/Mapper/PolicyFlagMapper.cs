using AgriForecast.Application.Requests.PolicyFlag.DTOs;
using AgriForecast.Domain.Entities;

namespace AgriForecast.Application.Mapper;

// Hand-written replacement for the former PolicyFlag object-mapper profile (PolicyFlag CreateMap set).
public static class PolicyFlagMapper
{
    // PolicyFlag_CreateDto -> PolicyFlag
    // Mirrors CreateMap<PolicyFlag_CreateDto, PolicyFlag>: new Guid Id; EffectiveFrom/EffectiveTo
    // normalised to date-only (leakage guard); EffectiveTo stays nullable; CreatedAtUtc = UtcNow.
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

    // PolicyFlag -> PolicyFlag_GetDto
    // Mirrors CreateMap<PolicyFlag, PolicyFlag_GetDto>() (convention-only, all same-name members).
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
