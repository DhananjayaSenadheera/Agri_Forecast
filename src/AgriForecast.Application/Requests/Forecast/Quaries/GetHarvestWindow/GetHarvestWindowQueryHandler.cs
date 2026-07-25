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

        // Today's price, so the panel can say whether the recommended window actually
        // beats selling now instead of leaving the farmer to compare it against the
        // OTHER screen's number. Same CurrentPriceRule as the harvest forecast, so the
        // two can never disagree; asOf is the date the sweep itself starts from, for
        // the same no-lookahead reason.
        //
        // Deliberately asymmetric with the ML call above: the window IS the payload,
        // so its absence is a real failure, whereas the current price only renders a
        // comparison the UI already hides when it is 0 ("unknown"). Losing a good
        // window because a secondary lookup timed out is the worse outcome, so an
        // infrastructure fault here degrades instead of failing.
        //
        // The filter stays narrow (transport/DB faults only, matching the typed
        // clients' `when (ex is ...)` convention): a NullReference or the like is a
        // bug and must still surface loudly rather than hide behind a plausible 0.
        // A cancelled request is not caught either — that response is going nowhere.
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
            // Visible to ops, invisible to the farmer: nothing from `ex` reaches the
            // response — the window returns normally with CurrentPrice = 0.
            _logger.LogWarning(ex,
                "Current price lookup failed for crop {CropId}; returning the window without the comparison.",
                request.CropId);
        }

        window.CurrentPrice = currentPrice;

        return Result<HarvestWindowDto>.Success(window);
    }
}
