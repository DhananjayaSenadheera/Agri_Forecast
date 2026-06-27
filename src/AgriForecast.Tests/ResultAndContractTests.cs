using AgriForecast.Application.common;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// Tests for the Result&lt;T&gt; discriminated union (success / failure / warnings).
/// These are pure value-type tests — no mocks needed.
/// </summary>
public class ResultTests
{
    // ──────────────────────────────────────────────────────────────────────────────
    // 1. Success factory
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Success_SetsIsSuccessTrue_AndDataAvailable()
    {
        var result = Result<string>.Success("hello");

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be("hello");
        result.Error.Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 2. Failure factory
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Failure_SetsIsSuccessFalse_AndErrorAvailable()
    {
        var result = Result<string>.Failure("something broke");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("something broke");
        result.Data.Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 3. SuccessWithWarnings
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SuccessWithWarnings_IsSuccess_AndWarningsAvailable()
    {
        var result = Result<int>.SuccessWithWarnings(42, "stale data");

        result.IsSuccess.Should().BeTrue();
        result.Data.Should().Be(42);
        result.Warnings.Should().Be("stale data");
        result.Error.Should().BeNull();
    }

    // ──────────────────────────────────────────────────────────────────────────────
    // 4. MlContract constants remain stable (regression guard)
    // ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MlContract_FallbackPredictor_IsExpectedString()
    {
        AgriForecast.Application.common.MlContract.FallbackPredictor
            .Should().Be("crop_mean_fallback",
                "changing this would break the honesty cap that guards against bad recommendations");
    }

    [Fact]
    public void MlContract_LowConfidence_IsExpectedString()
    {
        AgriForecast.Application.common.MlContract.LowConfidence
            .Should().Be("Low",
                "this must match the Python ML service confidence string exactly (case-insensitive compare in handler)");
    }
}
