using FluentValidation;

namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastAccuracySummary;

// windowDays in [1, 3650], validated rather than silently clamped.
//
// The lower bound is 1 because a zero- or negative-day window is not a narrower question, it is an empty
// one that would render as "no data" rather than as the caller error it is. The upper bound is ten
// years: past that the window stops bounding anything (the ledger is younger than that), and it exists
// so /summary can never be turned back into an unbounded scan of the whole table by query string.
public class GetForecastAccuracySummaryValidator : AbstractValidator<GetForecastAccuracySummaryQuery>
{
    public const int MinWindowDays = 1;
    public const int MaxWindowDays = 3650;

    public GetForecastAccuracySummaryValidator()
    {
        RuleFor(q => q.WindowDays)
            .InclusiveBetween(MinWindowDays, MaxWindowDays)
            .WithMessage($"windowDays must be between {MinWindowDays} and {MaxWindowDays}.");
    }
}
