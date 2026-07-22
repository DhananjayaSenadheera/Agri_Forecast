using AgriForecast.Application.Requests.Admin.Logs.Common;
using FluentValidation;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;

// Validates the user-activity query per the house posture (bad input -> 400 via ValidationBehavior ->
// GlobalExceptionMiddleware). page >= 1; pageSize in [1,100] (validated, not silently clamped); type
// optional but, when present, must be a known event-type wire string (case-insensitive) so a typo
// never silently returns an unfiltered page.
public class GetUserActivityValidator : AbstractValidator<GetUserActivityQuery>
{
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;

    public GetUserActivityValidator()
    {
        RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("page must be >= 1.");

        RuleFor(q => q.PageSize)
            .InclusiveBetween(MinPageSize, MaxPageSize)
            .WithMessage($"pageSize must be between {MinPageSize} and {MaxPageSize}.");

        RuleFor(q => q.Type)
            .Must(t => string.IsNullOrWhiteSpace(t) || UserActivityEventStrings.IsKnown(t))
            .WithMessage("type must be one of the known event types: "
                         + string.Join(", ", UserActivityEventStrings.KnownTypes) + ".");
    }
}
