using AgriForecast.Application.Requests.Forecast.Quaries.GetTimeline;
using FluentValidation;

namespace AgriForecast.Application.Requests.Forecast.Validators;

public class GetCropTimelineValidator : AbstractValidator<GetCropTimelineQuery>
{
    private const int MinMonths = 1;
    private const int MaxMonths = 24;

    public GetCropTimelineValidator()
    {
        RuleFor(q => q.CropId)
            .NotEqual(Guid.Empty).WithMessage("CropId is required.");

        RuleFor(q => q.Months)
            .InclusiveBetween(MinMonths, MaxMonths)
            .WithMessage($"Months must be between {MinMonths} and {MaxMonths}.");
    }
}
