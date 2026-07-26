using FluentValidation;

namespace AgriForecast.Application.Requests.Admin.Logs.Queries.GetTrainingRuns;

// page >= 1; pageSize in [1,100], validated rather than silently clamped.
public class GetTrainingRunsValidator : AbstractValidator<GetTrainingRunsQuery>
{
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;

    public GetTrainingRunsValidator()
    {
        RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("page must be >= 1.");

        RuleFor(q => q.PageSize)
            .InclusiveBetween(MinPageSize, MaxPageSize)
            .WithMessage($"pageSize must be between {MinPageSize} and {MaxPageSize}.");
    }
}
