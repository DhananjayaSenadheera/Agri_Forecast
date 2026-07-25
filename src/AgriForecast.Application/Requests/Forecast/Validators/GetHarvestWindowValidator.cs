using AgriForecast.Application.Requests.Forecast.Quaries.GetHarvestWindow;
using FluentValidation;

namespace AgriForecast.Application.Requests.Forecast.Validators;

public class GetHarvestWindowValidator : AbstractValidator<GetHarvestWindowQuery>
{
    // Mirrors the Python route's own bounds (HarvestWindowRequest.horizonDays).
    // Below a week there is no window to speak of; above a year the frozen
    // price/weather anchor is far too stale for the comparison to mean anything.
    private const int MinHorizonDays = 7;
    private const int MaxHorizonDays = 365;

    // AsOf window, same numbers and same typo-guard reasoning as
    // GetHarvestForecastValidator's PlantDate. AsOf now anchors the current-price
    // query as well as the sweep, so an absurd date silently returns a window
    // priced against nothing; reject it at the edge instead. The Python route
    // leaves asOf unbounded — being stricter here is the same deliberate choice
    // the forecast validator already makes.
    private const int MaxPastDays = 730;
    private const int MaxFutureDays = 365;

    public GetHarvestWindowValidator()
    {
        RuleFor(q => q.CropId)
            .NotEqual(Guid.Empty).WithMessage("CropId is required.");

        RuleFor(q => q.HorizonDays)
            .InclusiveBetween(MinHorizonDays, MaxHorizonDays)
            .WithMessage($"HorizonDays must be between {MinHorizonDays} and {MaxHorizonDays}.");

        // Omitted AsOf means "today" and is the normal case — only bound a supplied one.
        RuleFor(q => q.AsOf)
            .Must(BeWithinWindow)
            .When(q => q.AsOf.HasValue)
            .WithMessage($"AsOf must be within {MaxPastDays} days in the past and {MaxFutureDays} days in the future.");
    }

    private static bool BeWithinWindow(DateOnly? asOf)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        return asOf!.Value >= today.AddDays(-MaxPastDays)
               && asOf.Value <= today.AddDays(MaxFutureDays);
    }
}
