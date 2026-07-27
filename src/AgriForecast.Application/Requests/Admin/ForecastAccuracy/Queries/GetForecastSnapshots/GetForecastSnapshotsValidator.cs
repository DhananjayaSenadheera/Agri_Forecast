using FluentValidation;

namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastSnapshots;

// page >= 1; pageSize in [1,100], validated rather than silently clamped (house rule: the caller is told
// its request was wrong instead of quietly getting a different page than it asked for).
public class GetForecastSnapshotsValidator : AbstractValidator<GetForecastSnapshotsQuery>
{
    public const int MinPageSize = 1;
    public const int MaxPageSize = 100;

    // ForecastSnapshots.ModelVersion is nvarchar(20); a longer value cannot match any row, so it is a
    // caller error rather than a legitimately empty page.
    public const int MaxModelVersionLength = 20;

    public GetForecastSnapshotsValidator()
    {
        RuleFor(q => q.Page)
            .GreaterThanOrEqualTo(1)
            .WithMessage("page must be >= 1.");

        RuleFor(q => q.PageSize)
            .InclusiveBetween(MinPageSize, MaxPageSize)
            .WithMessage($"pageSize must be between {MinPageSize} and {MaxPageSize}.");

        // An all-zeroes GUID matches nothing and is almost always a client bug (an unset variable
        // serialised). Rejected loudly instead of returning a confusing empty page.
        RuleFor(q => q.CropId!.Value)
            .NotEqual(Guid.Empty)
            .When(q => q.CropId.HasValue)
            .WithMessage("cropId must not be an empty GUID.");

        RuleFor(q => q.ModelVersion!)
            .MaximumLength(MaxModelVersionLength)
            .When(q => !string.IsNullOrWhiteSpace(q.ModelVersion))
            .WithMessage($"modelVersion must be at most {MaxModelVersionLength} characters.");
    }
}
