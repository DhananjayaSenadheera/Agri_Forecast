using System.Data.Common;
using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetHarvestWindow;

public class GetHarvestWindowQueryHandler
    : IRequestHandler<GetHarvestWindowQuery, Result<HarvestWindowDto>>
{
    private readonly IHarvestPredictionClient _predictionClient;
    private readonly IMarketPriceRepository _marketPriceRepository;
    private readonly ILogger<GetHarvestWindowQueryHandler> _logger;

    public GetHarvestWindowQueryHandler(
        IHarvestPredictionClient predictionClient,
        IMarketPriceRepository marketPriceRepository,
        ILogger<GetHarvestWindowQueryHandler> logger)
    {
        _predictionClient = predictionClient;
        _marketPriceRepository = marketPriceRepository;
        _logger = logger;
    }

    public async Task<Result<HarvestWindowDto>> Handle(
        GetHarvestWindowQuery request, CancellationToken cancellationToken)
    {
        // Consume the ML /harvest-window response verbatim. Rankable=false is a SUCCESS, not a failure —
        // it is the honest "we cannot tell one date from another" answer and the UI has a state for it.
        // Only a null (ML service unreachable) is an error.
        var window = await _predictionClient.GetHarvestWindowAsync(
            request.CropId, request.AsOf, request.HorizonDays, cancellationToken);

        if (window == null)
        {
            _logger.LogWarning("Forecast service unavailable for crop {CropId}.", request.CropId);
            return Result<HarvestWindowDto>.Failure("Forecast service unavailable. Please try again later.");
        }

        // Today's price, from the same CurrentPriceRule as the harvest forecast so the two screens can
        // never quote different numbers; asOf is the sweep's start date for the same no-lookahead reason.
        //
        // Deliberately asymmetric with the ML call above: the window IS the payload, so its absence is a
        // real failure, whereas a missing current price only hides a comparison the UI already hides at 0.
        // The catch stays narrow (transport and DB faults only) so a genuine bug still surfaces loudly, and
        // a cancelled request is not caught either.
        decimal currentPrice = 0m;
        try
        {
            (currentPrice, _) = await CurrentPriceRule.ComputeAsync(
                _marketPriceRepository,
                request.CropId,
                request.AsOf ?? DateOnly.FromDateTime(DateTime.UtcNow),
                cancellationToken);
        }
        catch (Exception ex) when (ex is DbException or TimeoutException)
        {
            // Visible to ops, invisible to the farmer: nothing from ex reaches the response.
            _logger.LogWarning(ex,
                "Current price lookup failed for crop {CropId}; returning the window without the comparison.",
                request.CropId);
        }

        window.CurrentPrice = currentPrice;

        return Result<HarvestWindowDto>.Success(window);
    }
}
