using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Domain.Entities;
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
    private readonly IGenericRepository<CropAgronomyProfile> _agronomyProfileRepository;

    public CropCreateCommandHandler( CodeSettings codeSetting,
        IUnitofWorkRepository unitOfWork, ILogger<CropCreateCommandHandler> logger, ICropRepository cropRepository,
        IGenericRepository<CropAgronomyProfile> agronomyProfileRepository)
    {
        _codeSetting = codeSetting;
        _unitOfWork = unitOfWork;
        _logger = logger;
        _cropRepository = cropRepository;
        _agronomyProfileRepository = agronomyProfileRepository;
    }
    public async Task<Result<bool>> Handle(CropCreateCommand request, CancellationToken cancellationToken)
    {
        
        var dto = request.CreateDto;
        if (dto is null)
        {
            _logger.LogInformation("Failed to create crop: Crop details are null.");
            return Result<bool>.Failure("Crop details cannot be null.");
        }
        
        // CropCode prefix (VEG/FRT) follows the crop's TOP-LEVEL category (sub-categories roll up).
        var prefix = Domain.Entities.CropCategory.PrefixForCategory(dto.CropCategoryId);
        var cropcode = await _codeSetting.GetCropCode(prefix);
        if (cropcode is null)
        {
            _logger.LogInformation("Failed to create crop: Crop code is null.");
            return Result<bool>.Failure("Crop code cannot be null.");
        }

        var crop = dto.ToEntity();
        crop.CropCode = cropcode;
        await _cropRepository.Addasync(crop);

        // A crop must never exist without an agronomy profile. Stage a PENDING (unverified,
        // all-fields-NULL) profile in the SAME SaveChanges scope as the crop insert so the two
        // commit atomically — Step-5 curation later fills and verifies it.
        await _agronomyProfileRepository.AddAsync(CropAgronomyProfile.CreatePending(crop.Id));

        await _unitOfWork.CommitAsync();
        _logger.LogInformation("Crop created successfully with Crop Code: {CropCode} (pending agronomy profile staged).", crop.CropCode);
        return Result<bool>.Success(true);
    }
}