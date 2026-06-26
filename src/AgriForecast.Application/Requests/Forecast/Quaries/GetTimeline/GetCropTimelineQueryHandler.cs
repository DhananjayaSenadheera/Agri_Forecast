using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetTimeline;

public class GetCropTimelineQueryHandler
    : IRequestHandler<GetCropTimelineQuery, Result<CropTimelineDto>>
{
    private readonly IHarvestPredictionClient _predictionClient;
    private readonly ILogger<GetCropTimelineQueryHandler> _logger;

    public GetCropTimelineQueryHandler(
        IHarvestPredictionClient predictionClient,
        ILogger<GetCropTimelineQueryHandler> logger)
    {
        _predictionClient = predictionClient;
        _logger = logger;
    }

    public async Task<Result<CropTimelineDto>> Handle(
        GetCropTimelineQuery request, CancellationToken cancellationToken)
    {
        // Read-only: consume the Python ML service /timeline verbatim.
        // Crops with no history come back with empty history + Low confidence -
        // that is a valid response, not a failure; pass it straight through.
        var timeline = await _predictionClient.GetTimelineAsync(
            request.CropId, request.AsOf, request.Months, cancellationToken);

        if (timeline == null)
        {
            _logger.LogWarning("Forecast service unavailable for crop {CropId}.", request.CropId);
            return Result<CropTimelineDto>.Failure("Forecast service unavailable. Please try again later.");
        }

        return Result<CropTimelineDto>.Success(timeline);
    }
}
