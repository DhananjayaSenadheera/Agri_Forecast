using AgriForecast.Application.Requests.Forecast.Quaries.GetHarvest;
using FluentValidation;

namespace AgriForecast.Application.Requests.Forecast.Validators;

public class GetHarvestForecastValidator : AbstractValidator<GetHarvestForecastQuery>
{
    // Sane plant-date window. Lower bound allows backtests/late entry on recent
    // seasons; upper bound rejects absurd future planting (typo guard).
    private const int MaxPastDays = 730;   // ~2 years back
    private const int MaxFutureDays = 365; // ~1 year ahead

    public GetHarvestForecastValidator()
    {
        RuleFor(q => q.CropId)
            .NotEqual(Guid.Empty).WithMessage("CropId is required.");

        RuleFor(q => q.PlantDate)
            .Must(BeWithinWindow)
            .WithMessage($"PlantDate must be within {MaxPastDays} days in the past and {MaxFutureDays} days in the future.");
    }

    private static bool BeWithinWindow(DateOnly plantDate)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return plantDate >= today.AddDays(-MaxPastDays)
               && plantDate <= today.AddDays(MaxFutureDays);
    }
}
