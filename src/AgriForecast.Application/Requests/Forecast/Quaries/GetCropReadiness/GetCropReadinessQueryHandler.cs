using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Forecast.DTOs;
using AgriForecast.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetCropReadiness;

public class GetCropReadinessQueryHandler
    : IRequestHandler<GetCropReadinessQuery, Result<CropReadiness_GetDto>>
{
    private readonly IHarvestPredictionClient _predictionClient;
    private readonly ILogger<GetCropReadinessQueryHandler> _logger;

    public GetCropReadinessQueryHandler(
        IHarvestPredictionClient predictionClient,
        ILogger<GetCropReadinessQueryHandler> logger)
    {
        _predictionClient = predictionClient;
        _logger = logger;
    }

    public async Task<Result<CropReadiness_GetDto>> Handle(
        GetCropReadinessQuery request, CancellationToken cancellationToken)
    {
        // Read-only passthrough. The empty shape (ModelActive=false, no crops) is a valid response — the
        // FE degrades to no tint — so only a transport failure is a Failure.
        var readiness = await _predictionClient.GetCropReadinessAsync(cancellationToken);

        if (readiness == null)
        {
            _logger.LogWarning("Forecast service unavailable for crop readiness.");
            return Result<CropReadiness_GetDto>.Failure("Forecast service unavailable. Please try again later.");
        }

        var dto = new CropReadiness_GetDto
        {
            ModelVersion = readiness.ModelVersion,
            MinHistoryObs = readiness.MinHistoryObs,
            ModelActive = readiness.ModelActive,
        };
        foreach (var (key, entry) in readiness.Crops)
        {
            // Keys are lowercase GUID strings by trainer convention. A non-GUID key cannot be joined to a
            // Crop row, so skip it rather than failing the whole map.
            if (!Guid.TryParse(key, out var cropId))
            {
                _logger.LogWarning("Ignoring non-GUID crop key {Key} in ML readiness map.", key);
                continue;
            }
            dto.Crops.Add(new CropReadinessItem_GetDto
            {
                CropId = cropId,
                Ready = entry.Ready,
                NObs = entry.NObs,
            });
        }

        return Result<CropReadiness_GetDto>.Success(dto);
    }
}
