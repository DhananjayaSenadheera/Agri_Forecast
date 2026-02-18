using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Domain.Interfaces;
using AutoMapper;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Crop.Quaries.GetOneById;

public class CropGetByIdQueryHandler : IRequestHandler<CropGetByIdQuery, Result<Crop_GetDto>>
{
    private readonly ICropRepository _cropRepository;
    private readonly IMapper _mapper;
    private ILogger <CropGetByIdQueryHandler> _logger;
    
    public CropGetByIdQueryHandler(ICropRepository cropRepository, IMapper mapper, ILogger<CropGetByIdQueryHandler> logger)
    {
        _cropRepository = cropRepository;
        _mapper = mapper;
        _logger = logger;
    }
    public async Task<Result<Crop_GetDto>> Handle(CropGetByIdQuery request, CancellationToken cancellationToken)
    {
        if(request.Guid == null || request.Guid == Guid.Empty)
        {
            _logger.LogInformation("Failed to retrieve crop: Crop ID is null.");
            return Result<Crop_GetDto>.Failure("Crop ID cannot be null.");
        }
        var crop = await _cropRepository.GetByIdAsync(request.Guid);
        if (crop == null)        {
            _logger.LogInformation("Failed to retrieve crop: Crop with ID {CropId} does not exist.", request.Guid);
            return Result<Crop_GetDto>.Failure("Crop does not exist.");
        }
        var cropDto = _mapper.Map<Crop_GetDto>(crop);
        _logger.LogInformation("Crop with ID {CropId} retrieved successfully.", request.Guid);
        return Result<Crop_GetDto>.Success(cropDto);
    }
}