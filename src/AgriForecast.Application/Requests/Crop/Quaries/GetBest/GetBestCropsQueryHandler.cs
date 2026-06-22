using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Crop.DTOs;
using AgriForecast.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Crop.Quaries.GetBest;

public class GetBestCropsQueryHandler : IRequestHandler<GetBestCropsQuery, Result<List<BestCrop_GetDto>>>
{
    private readonly IRecommendationService _recommendationService;
    private readonly ILogger<GetBestCropsQueryHandler> _logger;

    public GetBestCropsQueryHandler(IRecommendationService recommendationService, ILogger<GetBestCropsQueryHandler> logger)
    {
        _recommendationService = recommendationService;
        _logger = logger;
    }

    public async Task<Result<List<BestCrop_GetDto>>> Handle(GetBestCropsQuery request, CancellationToken cancellationToken)
    {
        var crops = await _recommendationService.GetBestCropsAsync(request.LookbackMonths, cancellationToken);

        if (crops == null || !crops.Any())
        {
            _logger.LogInformation("No crop recommendation data available.");
            return Result<List<BestCrop_GetDto>>.Failure("No crop data available for recommendations.");
        }

        _logger.LogInformation("Retrieved {Count} crop recommendations.", crops.Count);
        return Result<List<BestCrop_GetDto>>.Success(crops);
    }
}
