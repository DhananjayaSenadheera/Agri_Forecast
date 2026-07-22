using FluentValidation;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetSystemErrors;

// Validates the system-errors query per the house posture (bad input -> 400 via ValidationBehavior ->
// GlobalExceptionMiddleware). page >= 1; pageSize in [1,100] (validated, not silently clamped).
public class GetSystemErrorsValidator : AbstractValidator<GetSystemErrorsQuery>
{
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;

    public GetSystemErrorsValidator()
    {
        RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("page must be >= 1.");

        RuleFor(q => q.PageSize)
            .InclusiveBetween(MinPageSize, MaxPageSize)
            .WithMessage($"pageSize must be between {MinPageSize} and {MaxPageSize}.");
    }
}
