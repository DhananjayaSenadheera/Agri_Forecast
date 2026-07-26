using AgriForecast.Application.Requests.Admin.Logs.Common;
using FluentValidation;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;

// page >= 1; pageSize in [1,100], validated rather than silently clamped; type optional but, when
// present, must be a known event-type wire string (case-insensitive) so a typo never returns an
// unfiltered page.
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

        // Every token must be known: one bad token 400s the whole request rather than being dropped, and
        // the message names the offending tokens so the error is actionable.
        RuleFor(q => q.Types)
            .Must(t => UnknownTokens(t).Count == 0)
            .WithMessage(q => "types contains unknown event type(s): "
                              + string.Join(", ", UnknownTokens(q.Types))
                              + ". Known event types: "
                              + string.Join(", ", UserActivityEventStrings.KnownTypes) + ".");
    }

    // Tokens of a ?types= list that are not known event types; blank tokens between commas are ignored.
    private static IReadOnlyList<string> UnknownTokens(string? types) =>
        UserActivityEventStrings.SplitTypes(types)
            .Where(t => !UserActivityEventStrings.IsKnown(t))
            .ToList();
}
