using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetHarvestWindow;

public class GetHarvestWindowQueryHandler
    : IRequestHandler<GetHarvestWindowQuery, Result<HarvestWindowDto>>
{
    private readonly IHarvestPredictionClient _predictionClient;
    private readonly ILogger<GetHarvestWindowQueryHandler> _logger;

    public GetHarvestWindowQueryHandler(
        IHarvestPredictionClient predictionClient,
        ILogger<GetHarvestWindowQueryHandler> logger)
    {
        _predictionClient = predictionClient;
        _logger = logger;
    }

    public async Task<Result<HarvestWindowDto>> Handle(
        GetHarvestWindowQuery request, CancellationToken cancellationToken)
    {
        // Read-only: consume the Python ML service /harvest-window verbatim.
        //
        // A Rankable=false response is a SUCCESS, not a failure — it is the honest
        // "we cannot tell one date from another for this crop" answer, and the UI
        // has a state for it. Only a null (ML service unreachable) is an error.
        // Do not "helpfully" convert one into the other in either direction.
        var window = await _predictionClient.GetHarvestWindowAsync(
            request.CropId, request.AsOf, request.HorizonDays, cancellationToken);

        if (window == null)
        {
            _logger.LogWarning("Forecast service unavailable for crop {CropId}.", request.CropId);
            return Result<HarvestWindowDto>.Failure("Forecast service unavailable. Please try again later.");
        }

        return Result<HarvestWindowDto>.Success(window);
    }
}
