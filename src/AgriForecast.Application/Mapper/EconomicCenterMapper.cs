using AgriForecast.Application.Requests.EcconomicCenter.DTOs;
using AgriForecast.Domain.Entities;

namespace AgriForecast.Application.Mapper;

// Hand-written replacement for the former EconomicCenter object-mapper profile (Eco CreateMap set).
public static class EconomicCenterMapper
{
    // Eco_CreateDto -> EconomicCenter
    // Mirrors CreateMap<Eco_CreateDto, EconomicCenter>: explicit Id (new Guid), Name and
    // UtcNow timestamps. Location and Description were NOT listed as ForMember but the old
    // same-name copy convention carried them, so they are copied here too. EcoCode set by handler.
    public static EconomicCenter ToEntity(this Eco_CreateDto src)
    {
        return EconomicCenter.CreateNew(
            name: src.Name,
            location: src.Location,
            description: src.Description);
    }

    // Eco_UpdateDto -> EconomicCenter (mutate-in-place onto the tracked entity)
    // Mirrors CreateMap<Eco_UpdateDto, EconomicCenter>: Name, Location, Description overwritten
    // unconditionally (no PreConditions in the original), UpdatedAt refreshed to UtcNow.
    public static EconomicCenter ApplyTo(this Eco_UpdateDto src, EconomicCenter destination)
    {
        destination.ApplyUpdate(
            name: src.Name!,
            location: src.Location!,
            description: src.Description!);
        destination.UpdatedAt = DateTime.UtcNow;
        return destination;
    }

    // EconomicCenter -> Eco_GetDto
    public static Eco_GetDto ToGetDto(this EconomicCenter src)
    {
        return new Eco_GetDto
        {
            Id = src.Id,
            Name = src.Name,
            Location = src.Location,
            Description = src.Description,
            CreatedAt = src.CreatedAt,
            UpdatedAt = src.UpdatedAt
        };
    }

    public static List<Eco_GetDto> ToGetDtoList(this IEnumerable<EconomicCenter> src)
    {
        return src.Select(e => e.ToGetDto()).ToList();
    }
}
