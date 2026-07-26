using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Forecast.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Forecast.Quaries.GetHarvest;

public class GetHarvestForecastQueryHandler
    : IRequestHandler<GetHarvestForecastQuery, Result<HarvestForecast_GetDto>>
{
    // The current-price rule lives in CurrentPriceRule so the best-harvest-window screen computes the
    // identical number — the two screens are read side by side.

    // If the freshest servable price is older than this before the plant date, the signal is low-trust.
    private const int StaleAfterDays = 90;

    private readonly IMarketPriceRepository _marketPriceRepository;
    private readonly IHarvestPredictionClient _predictionClient;
    private readonly ILogger<GetHarvestForecastQueryHandler> _logger;

    public GetHarvestForecastQueryHandler(
        IMarketPriceRepository marketPriceRepository,
        IHarvestPredictionClient predictionClient,
        ILogger<GetHarvestForecastQueryHandler> logger)
    {
        _marketPriceRepository = marketPriceRepository;
        _predictionClient = predictionClient;
        _logger = logger;
    }

    public async Task<Result<HarvestForecast_GetDto>> Handle(
        GetHarvestForecastQuery request, CancellationToken cancellationToken)
    {
        // a. Current price as of the PLANT date, so a historical plant date cannot see later prices.
        var (currentPrice, latestObservation) = await CurrentPriceRule.ComputeAsync(
            _marketPriceRepository, request.CropId, request.PlantDate, cancellationToken);

        // b. Call the Python ML service. Fail safe on null.
        var prediction = await _predictionClient.PredictAsync(
            request.CropId, request.PlantDate, cancellationToken);

        if (prediction == null)
        {
            _logger.LogWarning("Forecast service unavailable for crop {CropId}.", request.CropId);
            return Result<HarvestForecast_GetDto>.Failure("Forecast service unavailable. Please try again later.");
        }

        // Staleness guard (optional): older-than-90-days data caps trust.
        bool staleData = latestObservation.HasValue
            && latestObservation.Value < request.PlantDate.AddDays(-StaleAfterDays);

        // c. Apply the recommendation matrix.
        var (level, reason, upside, width, lowTrust) = Recommend(prediction, currentPrice, staleData);

        // d. Assemble the farmer-facing DTO (model fields pass through untouched).
        var dto = new HarvestForecast_GetDto
        {
            CropId = request.CropId,
            CropName = prediction.CropName,
            PlantDate = request.PlantDate,
            HarvestDate = prediction.HarvestDate,
            GrowthPeriodDays = prediction.GrowthPeriodDays,
            CurrentPrice = currentPrice,
            PredictedPrice = prediction.PredictedPrice,
            LowerBound = prediction.LowerBound,
            UpperBound = prediction.UpperBound,
            Confidence = prediction.Confidence,
            ActivePredictor = prediction.ActivePredictor,
            ModelVersion = prediction.ModelVersion,
            Explanation = prediction.Explanation,
                // Additive ML fields pass straight through; TopFactors stays null when the ML omitted it.
            ReasonCode = prediction.ReasonCode,
            ReasonParams = prediction.ReasonParams,
            TopFactors = prediction.TopFactors,
            RecommendationLevel = level,
            Reason = reason,
            UpsidePct = Math.Round(upside * 100m, 1),
            IntervalWidthPct = Math.Round(width * 100m, 1),
            LowTrust = lowTrust
        };

        return Result<HarvestForecast_GetDto>.Success(dto);
    }

    // Faithful implementation of agri-ml-engineer's recommendation rule.
    private static (RecommendationLevel level, string reason, decimal upside, decimal width, bool lowTrust)
        Recommend(HarvestPredictionDto p, decimal currentPrice, bool staleData)
    {
        if (currentPrice <= 0 || p.PredictedPrice <= 0)
            return (RecommendationLevel.NotRecommended, "Insufficient price data to make a recommendation.", 0m, 0m, true);

        if (p.LowerBound > p.PredictedPrice || p.PredictedPrice > p.UpperBound)
            return (RecommendationLevel.NotRecommended, "Forecast interval invalid - cannot recommend.", 0m, 0m, true);

        var upside = (p.PredictedPrice - currentPrice) / currentPrice;
        var width = (p.UpperBound - p.LowerBound) / p.PredictedPrice;

        // A fallback, Low-confidence or stale signal is never strongly recommended. Compare against
        // MlContract with OrdinalIgnoreCase so the cap fails closed if the ML wording or casing drifts.
        bool lowTrust = string.Equals(p.ActivePredictor, MlContract.FallbackPredictor, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Confidence, MlContract.LowConfidence, StringComparison.OrdinalIgnoreCase)
                        || staleData;

        var ceiling = RecommendationLevel.StronglyRecommended;
        if (lowTrust) ceiling = RecommendationLevel.Recommended;
        if (width > 0.60m) ceiling = Min(ceiling, RecommendationLevel.RecommendedWithRisk);

        RecommendationLevel raw;
        string reason;
        if (upside < -0.05m)
        {
            raw = RecommendationLevel.NotRecommended;
            reason = "Forecast below today's price - consider another crop.";
        }
        else if (upside < 0.08m)
        {
            raw = RecommendationLevel.RecommendedWithRisk;
            reason = "Roughly flat versus today - limited upside.";
        }
        else if (upside < 0.20m)
        {
            raw = (width <= 0.30m) ? RecommendationLevel.Recommended : RecommendationLevel.RecommendedWithRisk;
            reason = (width <= 0.30m)
                ? "Harvest price forecast above today's - favorable."
                : "Higher harvest price likely, but the forecast is uncertain.";
        }
        else
        {
            raw = (width <= 0.30m)
                ? RecommendationLevel.StronglyRecommended
                : (width <= 0.60m ? RecommendationLevel.Recommended : RecommendationLevel.RecommendedWithRisk);
            reason = (width <= 0.30m)
                ? "Strong harvest-price upside with a tight forecast - a good bet."
                : "Large potential upside, but the forecast is wide - some risk.";
        }

        var capped = Min(raw, ceiling);

        // If trust capped the recommendation below the raw signal, be honest about why.
        if ((int)capped < (int)raw)
        {
            reason = staleData
                ? reason + " (Based on older data - treat with caution.)"
                : "Forecast too uncertain to recommend confidently.";
        }

        return (capped, reason, upside, width, lowTrust);
    }

    private static RecommendationLevel Min(RecommendationLevel a, RecommendationLevel b)
        => (int)a <= (int)b ? a : b;
}
