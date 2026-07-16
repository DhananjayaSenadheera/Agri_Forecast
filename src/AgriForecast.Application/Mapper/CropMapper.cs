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
    // API-3 enrichment: the Crop entity deliberately has no navigation properties, so the
    // caller (query handler) supplies the already-loaded CropCategory and CropAgronomyProfile.
    // Both are optional (null => that projection is null), which keeps the existing callers
    // and the crop-only unit tests working unchanged.
    public static Crop_GetDto ToGetDto(this Crop src, CropCategory? category = null, CropAgronomyProfile? profile = null)
    {
        return new Crop_GetDto
        {
            Id = src.Id,
            CropCode = src.CropCode,
            Name = src.Name,
            Source = src.Source,
            Category = category is null
                ? null
                : new CropCategory_GetDto { Code = category.Code, Name = category.Name },
            GrowthDays = ResolveGrowthDays(profile),
            CreatedAt = src.CreatedAt,
            UpdatedAt = src.UpdatedAt
        };
    }

    // growthDays serving gate (load-bearing): expose GrowthPeriodDays ONLY when the crop is
    // verified AND has a positive growth period. This mirrors the Python serving gate so the
    // UI never shows a growth period the forecaster refuses to use. Everything else => null:
    //   * no profile row            -> null
    //   * IsVerified == false       -> null (the 2 held-unverified crops)
    //   * GrowthPeriodDays == null  -> null (continuous/perennial crops: Coconut, Papaya, …)
    //   * GrowthPeriodDays <= 0      -> null
    private static int? ResolveGrowthDays(CropAgronomyProfile? profile)
    {
        if (profile is null || !profile.IsVerified) return null;
        return profile.GrowthPeriodDays is int gp && gp > 0 ? gp : null;
    }

    // Enriched list mapping. Lookups are keyed by CropCategories.Id (4 rows) and
    // CropAgronomyProfiles.CropId (1:1). Missing keys map to null for that projection.
    // Both dictionaries are optional so the crop-only callers/tests keep working.
    public static List<Crop_GetDto> ToGetDtoList(
        this IEnumerable<Crop> src,
        IReadOnlyDictionary<Guid, CropCategory>? categoriesById = null,
        IReadOnlyDictionary<Guid, CropAgronomyProfile>? profilesByCropId = null)
    {
        return src.Select(c =>
        {
            CropCategory? category = null;
            if (c.CropCategoryId is Guid catId && categoriesById is not null)
            {
                categoriesById.TryGetValue(catId, out category);
            }

            CropAgronomyProfile? profile = null;
            profilesByCropId?.TryGetValue(c.Id, out profile);

            return c.ToGetDto(category, profile);
        }).ToList();
    }
}
