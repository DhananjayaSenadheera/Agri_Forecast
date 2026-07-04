using AgriForecast.Domain.Entities;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// Guards for the MacroSeriesPoint domain entity (R1 P3 CBSL macro, DECISIONS.md 2026-07-04).
/// The load-bearing invariants are the VINTAGE rules:
///   - a vintage (PublishedAt) can never precede the period it describes (ReferenceDate),
///   - both dates are stored date-only (no hidden time can flip the day-boundary comparison),
///   - NO positive-Value guard (YoY / change series can legitimately be <= 0, unlike a price/FX).
/// Style mirrors the pure-domain ctor discipline in this project (cf. FestivalCalendarSeedTests /
/// MarketDomainTests): no EF round-trip, deterministic, no wall-clock.
/// </summary>
public class MacroSeriesPointTests
{
    private static readonly DateTime Retrieved = new(2026, 07, 04, 12, 0, 0, DateTimeKind.Utc);

    private static MacroSeriesPoint Valid(
        string seriesCode = "CCPI_BASE2021",
        DateTime? reference = null,
        DateTime? published = null,
        decimal value = 100.5m,
        string source = "CBSL press release",
        bool imputed = false)
        => new(
            seriesCode,
            reference ?? new DateTime(2026, 04, 01),
            published ?? new DateTime(2026, 04, 30),
            value,
            source,
            imputed,
            Retrieved);

    [Fact]
    public void Ctor_ValidVintage_ConstructsAndKeepsAllFields()
    {
        var p = Valid();

        p.SeriesCode.Should().Be("CCPI_BASE2021");
        p.ReferenceDate.Should().Be(new DateTime(2026, 04, 01));
        p.PublishedAt.Should().Be(new DateTime(2026, 04, 30));
        p.Value.Should().Be(100.5m);
        p.Source.Should().Be("CBSL press release");
        p.IsPublishedAtImputed.Should().BeFalse();
        p.RetrievedAtUtc.Should().Be(Retrieved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_BlankSeriesCode_Throws(string? code)
    {
        var act = () => Valid(seriesCode: code!);
        act.Should().Throw<ArgumentException>().WithParameterName("seriesCode");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Ctor_BlankSource_Throws(string? source)
    {
        var act = () => Valid(source: source!);
        act.Should().Throw<ArgumentException>().WithParameterName("source");
    }

    [Fact]
    public void Ctor_ReferenceDateAfterPublishedAt_Throws()
    {
        // A vintage cannot precede the period it describes: a May figure published in April is lookahead.
        var act = () => Valid(
            reference: new DateTime(2026, 05, 01),
            published: new DateTime(2026, 04, 30));

        act.Should().Throw<ArgumentException>().WithParameterName("publishedAt");
    }

    [Fact]
    public void Ctor_PublishedAtEqualsReferenceDate_IsAllowed()
    {
        // Same-day publication is legitimate — the guard is strict-precedence, not strict-less-than.
        var day = new DateTime(2026, 04, 15);
        var p = Valid(reference: day, published: day);

        p.ReferenceDate.Should().Be(day);
        p.PublishedAt.Should().Be(day);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.8)]
    [InlineData(-100.123456)]
    public void Ctor_NonPositiveValue_IsAccepted(double value)
    {
        // YoY inflation / index change can be zero or negative (deflation / falling index).
        // There is deliberately NO positive-Value guard (unlike EconomicIndicator's FX rate).
        var p = Valid(value: (decimal)value);
        p.Value.Should().Be((decimal)value);
    }

    [Fact]
    public void Ctor_NormalizesBothDates_StripsHiddenTimeComponent()
    {
        var p = Valid(
            reference: new DateTime(2026, 04, 01, 9, 30, 15),
            published: new DateTime(2026, 04, 30, 23, 59, 59));

        p.ReferenceDate.Should().Be(new DateTime(2026, 04, 01));
        p.ReferenceDate.TimeOfDay.Should().Be(TimeSpan.Zero);
        p.PublishedAt.Should().Be(new DateTime(2026, 04, 30));
        p.PublishedAt.TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Ctor_NormalizesDatesBeforeInvariantCheck_HiddenTimeCannotFlipSameDay()
    {
        // Same calendar day, but a raw comparison of reference(23:59) > published(00:00) would
        // wrongly throw. Normalization-before-check must make same-day vintages valid.
        var act = () => Valid(
            reference: new DateTime(2026, 04, 15, 23, 59, 59),
            published: new DateTime(2026, 04, 15, 0, 0, 0));

        act.Should().NotThrow();
    }

    [Fact]
    public void Ctor_ImputedFlag_IsPassedThrough_NeverUpgraded()
    {
        var p = Valid(imputed: true);
        p.IsPublishedAtImputed.Should().BeTrue("an imputed vintage flag must pass through, never be silently upgraded");
    }
}
