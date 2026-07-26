using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Domain.Entities;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Crop.Quaries.GetAll;

public class CropGetAllQueryHandler : IRequestHandler<CropGetAllQuery, Result<List<Crop_GetDto>>>
{
    private readonly ICropRepository _cropRepository;
    // Crop has no navigation properties, so the reference tables are loaded directly and joined in memory.
    private readonly IGenericRepository<CropCategory> _categoryRepository;
    private readonly IGenericRepository<CropAgronomyProfile> _profileRepository;
    private readonly ILogger<CropGetAllQueryHandler> _logger;

    public CropGetAllQueryHandler(
        ICropRepository cropRepository,
        IGenericRepository<CropCategory> categoryRepository,
        IGenericRepository<CropAgronomyProfile> profileRepository,
        ILogger<CropGetAllQueryHandler> logger)
    {
        _cropRepository = cropRepository;
        _categoryRepository = categoryRepository;
        _profileRepository = profileRepository;
        _logger = logger;
    }

    public async Task<Result<List<Crop_GetDto>>> Handle(CropGetAllQuery request, CancellationToken cancellationToken)
    {
        var crops = await _cropRepository.GetAllAsync();
        if (crops == null || !crops.Any())
        {
            _logger.LogInformation("No crops found in the database.");
            return Result<List<Crop_GetDto>>.Failure("No crops found.");
        }

        var categoriesById = (await _categoryRepository.GetAllAsync())
            .ToDictionary(c => c.Id);
        var profilesByCropId = (await _profileRepository.GetAllAsync())
            .ToDictionary(p => p.CropId);

        var cropDtos = crops.ToGetDtoList(categoriesById, profilesByCropId);
        _logger.LogInformation("Successfully retrieved {CropCount} crops.", cropDtos.Count);
        return Result<List<Crop_GetDto>>.Success(cropDtos);

    }
}