using AgriForecast.Application.common;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Crop.Commands.Delete;

public class CropDeleteCommandHandler : IRequestHandler<CropDeleteCommand, Result<bool>>
{
    private readonly ICropRepository _cropRepository;
    private readonly IUnitofWorkRepository _unitOfWork;
    private readonly ILogger<CropDeleteCommandHandler> _logger;

    public CropDeleteCommandHandler(ICropRepository cropRepository, IUnitofWorkRepository unitOfWork, ILogger<CropDeleteCommandHandler> logger)
    {
        _cropRepository = cropRepository;
        _unitOfWork = unitOfWork;
        _logger = logger;
    }
    public async Task<Result<bool>> Handle(CropDeleteCommand request, CancellationToken cancellationToken)
    {
        if (request.Id != Guid.Empty)
        {
            var existingCrop = await _cropRepository.GetByIdAsync(request.Id);
            if (existingCrop == null)
            {
                _logger.LogInformation("Failed to delete crop: Crop with ID {CropId} does not exist.", request.Id);
                return Result<bool>.Failure("Crop does not exist.");
            }
            await _cropRepository.DeleteAsync(existingCrop);
            await _unitOfWork.CommitAsync();
            _logger.LogInformation("Crop with ID {CropId} deleted successfully.", request.Id);
            return Result<bool>.Success(true);
        }
        else
        {
            _logger.LogInformation("Failed to delete crop: Invalid Crop ID provided.");
            return Result<bool>.Failure("Invalid Crop ID.");
        }
    }
}