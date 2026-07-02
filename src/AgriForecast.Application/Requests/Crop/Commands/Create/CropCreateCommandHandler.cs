using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Crop.Commands.Create;

public class CropCreateCommandHandler : IRequestHandler<CropCreateCommand, Result<bool>>
{
    private readonly CodeSettings _codeSetting;
    private ILogger<CropCreateCommandHandler> _logger;
    private readonly IUnitofWorkRepository _unitOfWork;
    private readonly ICropRepository _cropRepository;

    public CropCreateCommandHandler( CodeSettings codeSetting,
        IUnitofWorkRepository unitOfWork, ILogger<CropCreateCommandHandler> logger, ICropRepository cropRepository)
    {
        _codeSetting = codeSetting;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _cropRepository = cropRepository;
    }
    public async Task<Result<bool>> Handle(CropCreateCommand request, CancellationToken cancellationToken)
    {
        
        var dto = request.CreateDto;
        if (dto is null)
        {
            _logger.LogInformation("Failed to create crop: Crop details are null.");
            return Result<bool>.Failure("Crop details cannot be null.");
        }
        
        var cropcode = await _codeSetting.GetCropCode();
        if (cropcode is null)
        {
            _logger.LogInformation("Failed to create crop: Crop code is null.");
            return Result<bool>.Failure("Crop code cannot be null."); 
        }
        
        var crop = dto.ToEntity();
        crop.CropCode = cropcode;
        await _cropRepository.Addasync(crop);
        await _unitOfWork.CommitAsync();
        _logger.LogInformation("Crop created successfully with Crop Code: {CropCode}", crop.CropCode);
        return Result<bool>.Success(true);
    }
}