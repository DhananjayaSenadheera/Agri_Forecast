using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Domain.Entities;

namespace AgriForecast.Application.Mapper;

// Hand-written replacement for the former Crop object-mapper profile (Crop CreateMap set).
// Static, allocation-only mapping; no reflection, no package dependency.
public static class CropMapper
{
    // Crop_CreateDto -> Crop
    // Mirrors CreateMap<Crop_CreateDto, Crop>: new Guid Id, UtcNow timestamps,
    // straight copy of Name / Source / CropCategoryId. CropCode is set by the handler.
    // CropCategoryId is validated (NotEmpty + must exist) upstream. (R2 Step 8.2 dropped
    // ExternalProductId — source-product mapping now lives in CommodityAliases.)
    public static Crop ToEntity(this Crop_CreateDto src)
    {
        return Crop.CreateForManualEntry(
            name: src.Name,
            source: src.Source,
            cropCategoryId: src.CropCategoryId);
    }

    // Crop_UpdateDto -> Crop (mutate-in-place onto the tracked entity)
    // Mirrors CreateMap<Crop_UpdateDto, Crop>: Name always overwritten; Source only
    // overwritten when the update actually supplies it (PreCondition); UpdatedAt refreshed
    // to UtcNow. Id is not reassigned (it identifies the tracked row).
    public static Crop ApplyTo(this Crop_UpdateDto src, Crop destination)
    {
        // The old ForMember(Name, MapFrom(src.Name)) copied the value unconditionally,
        // including null. Preserved exactly (src.Name is string?, destination.Name is string).
        destination.Name = src.Name!;

        // PreCondition(src => src.Source != null)
        if (src.Source != null)
        {
            destination.Source = src.Source;
        }

        destination.UpdatedAt = DateTime.UtcNow;
        return destination;
    }

    // Crop -> Crop_GetDto
    // Mirrors CreateMap<Crop, Crop_GetDto>: straight copy of the exposed fields.
    public static Crop_GetDto ToGetDto(this Crop src)
    {
        return new Crop_GetDto
        {
            Id = src.Id,
            Name = src.Name,
            Source = src.Source,
            CreatedAt = src.CreatedAt,
            UpdatedAt = src.UpdatedAt
        };
    }

    public static List<Crop_GetDto> ToGetDtoList(this IEnumerable<Crop> src)
    {
        return src.Select(c => c.ToGetDto()).ToList();
    }
}
