using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Domain.Interfaces;
using AutoMapper;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Crop.Quaries.GetAll;

public class CropGetAllQueryHandler : IRequestHandler<CropGetAllQuery, Result<List<Crop_GetDto>>>
{
    private readonly ICropRepository _cropRepository;
    private readonly IMapper _mapper;
    private readonly ILogger<CropGetAllQueryHandler> _logger;
    
    public CropGetAllQueryHandler(ICropRepository cropRepository, IMapper mapper, ILogger<CropGetAllQueryHandler> logger)
    {
        _cropRepository = cropRepository;
        _mapper = mapper;
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
        var cropDtos = _mapper.Map<List<Crop_GetDto>>(crops);
        _logger.LogInformation("Successfully retrieved {CropCount} crops.", cropDtos.Count);
        return Result<List<Crop_GetDto>>.Success(cropDtos);
        
    }
}