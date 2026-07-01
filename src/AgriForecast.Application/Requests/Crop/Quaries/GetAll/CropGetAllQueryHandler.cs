using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Crop.Quaries.GetAll;

public class CropGetAllQueryHandler : IRequestHandler<CropGetAllQuery, Result<List<Crop_GetDto>>>
{
    private readonly ICropRepository _cropRepository;
    private readonly ILogger<CropGetAllQueryHandler> _logger;

    public CropGetAllQueryHandler(ICropRepository cropRepository, ILogger<CropGetAllQueryHandler> logger)
    {
        _cropRepository = cropRepository;
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
        var cropDtos = crops.ToGetDtoList();
        _logger.LogInformation("Successfully retrieved {CropCount} crops.", cropDtos.Count);
        return Result<List<Crop_GetDto>>.Success(cropDtos);
        
    }
}