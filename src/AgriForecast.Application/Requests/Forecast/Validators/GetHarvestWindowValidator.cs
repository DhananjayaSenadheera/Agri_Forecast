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

    public GetHarvestWindowValidator()
    {
        RuleFor(q => q.CropId)
            .NotEqual(Guid.Empty).WithMessage("CropId is required.");

        RuleFor(q => q.HorizonDays)
            .InclusiveBetween(MinHorizonDays, MaxHorizonDays)
            .WithMessage($"HorizonDays must be between {MinHorizonDays} and {MaxHorizonDays}.");
    }
}
