using AgriForecast.Application.common;
using AgriForecast.Domain.Interfaces;
using AutoMapper;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Crop.Commands.Update;

public class CropUpdateCommandHandler : IRequestHandler<CropUpdateCommand, Result<bool>>
{
    private readonly ICropRepository _cropRepository;
    private readonly IMapper _mapper;
    private readonly IUnitofWorkRepository _unitOfWork;
    private ILogger <CropUpdateCommandHandler> _logger;
    
    public CropUpdateCommandHandler(ICropRepository cropRepository, IMapper mapper, IUnitofWorkRepository unitOfWork, ILogger<CropUpdateCommandHandler> logger)
    {
        _cropRepository = cropRepository;
        _mapper = mapper;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }
    public async Task<Result<bool>> Handle(CropUpdateCommand request, CancellationToken cancellationToken)
    {
        var requestDto = request.CropUpdateDto;
        if (requestDto is null) {
                _logger.LogInformation("Failed to update crop: Crop details are null.");
            return Result<bool>.Failure("Crop details cannot be null.");
        }
        
        var existingCrop    =  _cropRepository.GetByIdAsync(requestDto.Id).Result;  
        if (existingCrop == null)
        {
            _logger.LogInformation("Failed to update crop: Crop with ID {CropId} does not exist.", requestDto.Id);
            return Result<bool>.Failure("Crop does not exist.");
        }
        var crop = _mapper.Map(requestDto, existingCrop);
        await _cropRepository.UpdateAsync(crop);
        await _unitOfWork.CommitAsync();
        _logger.LogInformation("Crop with ID {CropId} updated successfully.", crop.Id);
        return Result<bool>.Success(true);
    }
}