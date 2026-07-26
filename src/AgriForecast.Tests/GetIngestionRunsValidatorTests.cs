using AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionRuns;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// GetIngestionRunsValidator bounds and source-key validation: page >= 1; pageSize in [1,100], validated
/// rather than clamped; source optional but, when present, a known ingestion source (case-insensitive).
/// </summary>
public class GetIngestionRunsValidatorTests
{
    private static readonly GetIngestionRunsValidator Validator = new();

    private static bool IsValid(int page, int pageSize, string? source = null)
        => Validator.Validate(new GetIngestionRunsQuery { Page = page, PageSize = pageSize, Source = source })
            .IsValid;

    // page.
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(99, true)]
    public void Page_LowerBound(int page, bool expectedValid)
        => IsValid(page, 20).Should().Be(expectedValid);

    // pageSize.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    [InlineData(1000, false)]
    public void PageSize_Bounds(int pageSize, bool expectedValid)
        => IsValid(1, pageSize).Should().Be(expectedValid);

    // source.
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("HARTI", true)]
    [InlineData("DAMBULLA_DEC", true)]
    [InlineData("harti", true)]        // case-insensitive
    [InlineData("cbsl_macro", true)]
    [InlineData("BOGUS", false)]
    [InlineData("DAMBULLA", false)]    // partial is not a match
    public void Source_KnownOnly(string? source, bool expectedValid)
        => IsValid(1, 20, source).Should().Be(expectedValid);
}
