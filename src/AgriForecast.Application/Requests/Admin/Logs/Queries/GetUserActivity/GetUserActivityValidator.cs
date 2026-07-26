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

        // types: optional comma-separated list. EVERY token must be known — one bad token 400s the
        // whole request rather than being dropped, so an admin can never mistake a silently-narrowed
        // page for the complete one. The message names the offending token(s), because "one of these
        // ten is wrong" is not an actionable error.
        RuleFor(q => q.Types)
            .Must(t => UnknownTokens(t).Count == 0)
            .WithMessage(q => "types contains unknown event type(s): "
                              + string.Join(", ", UnknownTokens(q.Types))
                              + ". Known event types: "
                              + string.Join(", ", UserActivityEventStrings.KnownTypes) + ".");
    }

    // Tokens of a ?types= list that are not known event types. A null/blank list has none (absent
    // filter), and blank tokens between commas are ignored by SplitTypes rather than rejected.
    private static IReadOnlyList<string> UnknownTokens(string? types) =>
        UserActivityEventStrings.SplitTypes(types)
            .Where(t => !UserActivityEventStrings.IsKnown(t))
            .ToList();
}
