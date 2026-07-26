using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Domain.Entities;

namespace AgriForecast.Application.Mapper;

// Hand-written static mappers for Crop (the house style — no AutoMapper).
public static class CropMapper
{
    // CropCode is stamped by the handler; CropCategoryId is validated upstream.
    public static Crop ToEntity(this Crop_CreateDto src)
    {
        return Crop.CreateForManualEntry(
            name: src.Name,
            source: src.Source,
            cropCategoryId: src.CropCategoryId);
    }

    // In-place update on the tracked entity. Source is only overwritten when the update supplies it.
    public static Crop ApplyTo(this Crop_UpdateDto src, Crop destination)
    {
        // Name is copied unconditionally, including null (matching the previous mapping).
        destination.Name = src.Name!;

        if (src.Source != null)
        {
            destination.Source = src.Source;
        }

        destination.UpdatedAt = DateTime.UtcNow;
        return destination;
    }

    // Crop has no navigation properties, so the caller passes the already-loaded CropCategory and
    // CropAgronomyProfile. Both are optional; null means that projection comes back null.
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

    // Expose GrowthPeriodDays only when the crop is verified and the value is positive. This mirrors the
    // Python serving gate, so the UI never shows a growth period the forecaster refuses to use.
    // Everything else (no profile, unverified, null, or non-positive) maps to null.
    private static int? ResolveGrowthDays(CropAgronomyProfile? profile)
    {
        if (profile is null || !profile.IsVerified) return null;
        return profile.GrowthPeriodDays is int gp && gp > 0 ? gp : null;
    }

    // Enriched list mapping. Lookups are keyed by CropCategories.Id and CropAgronomyProfiles.CropId;
    // both dictionaries are optional and a missing key maps to null.
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
