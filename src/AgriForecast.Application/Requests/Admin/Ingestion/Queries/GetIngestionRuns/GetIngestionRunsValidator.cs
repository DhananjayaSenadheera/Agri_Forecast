using AgriForecast.Domain.Constants;
using FluentValidation;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionRuns;

// page >= 1; pageSize in [1,100], validated rather than silently clamped; source optional but, when
// present, must be a known ingestion source key.
public class GetIngestionRunsValidator : AbstractValidator<GetIngestionRunsQuery>
{
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;

    public GetIngestionRunsValidator()
    {
        RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("page must be >= 1.");

        RuleFor(q => q.PageSize)
            .InclusiveBetween(MinPageSize, MaxPageSize)
            .WithMessage($"pageSize must be between {MinPageSize} and {MaxPageSize}.");

        // A present-but-unknown source is a 400, so a typo never silently returns an empty page.
        // Membership is case-insensitive.
        RuleFor(q => q.Source)
            .Must(s => string.IsNullOrWhiteSpace(s) || IngestionSources.IsKnown(s))
            .WithMessage("source must be one of the known ingestion sources: "
                         + string.Join(", ", IngestionSources.KnownKeys) + ".");
    }
}
