using AgriForecast.Domain.Entities;
using AgriForecast.Infrastructure.Database;
using FluentAssertions;

namespace AgriForecast.Tests;

/// <summary>
/// Guards for the FestivalCalendarEntry HasData seed. The load-bearing invariant is that the seed spans the
/// FULL training history (2015 to current+1) for every seeded festival: a forward-only seed would zero the
/// festival feature for most training rows while cross-validation still looked fine.
/// Tests read the exact rows that land in HasData via AgriForecastDbContext.GetFestivalCalendarSeed(), and
/// the provisional boundary is asserted against a PINNED reference year, never DateTime.Now.
/// </summary>
public class FestivalCalendarSeedTests
{
    // Pinned reference year = the last gazette-confirmed year in the seed. Anything after this is
    // provisional. Pinned (not DateTime.Now.Year) so the suite is deterministic across wall-clock.
    private const int LastConfirmedYear = 2026;

    // Seed span the tests assert against (mirrors the seeder's firstYear..lastYear).
    private const int FirstYear = 2015;
    private const int LastYear = 2030;

    // The festivals that must be seeded across the whole span.
    private static readonly string[] SeededFestivals = { "AVURUDU", "CHRISTMAS", "THAI_PONGAL" };

    private static IReadOnlyList<FestivalCalendarEntry> Seed()
        => AgriForecastDbContext.GetFestivalCalendarSeed();

    [Fact]
    public void Seed_CoversEveryYear_ThroughReferencePlusOne_ForEachFestival()
    {
        var seed = Seed();

        // "current+1" relative to the pinned reference year — the seed must reach at least here so
        // no near-future training/serving row is left without a festival signal.
        var requiredThrough = LastConfirmedYear + 1;

        foreach (var key in SeededFestivals)
        {
            var years = seed.Where(r => r.FestivalKey == key).Select(r => r.Date.Year).ToHashSet();

            for (var y = FirstYear; y <= requiredThrough; y++)
            {
                years.Should().Contain(y,
                    $"{key} must be seeded for {y} (seed must span 2015..current+1 or the feature is zeroed for that year)");
            }
        }
    }

    [Fact]
    public void Seed_SpansFullConfiguredRange_2015To2030()
    {
        var seed = Seed();

        foreach (var key in SeededFestivals)
        {
            var years = seed.Where(r => r.FestivalKey == key).Select(r => r.Date.Year).ToList();
            years.Min().Should().Be(FirstYear);
            years.Max().Should().Be(LastYear);
        }
    }

    [Fact]
    public void Seed_HasNoDuplicate_FestivalKeyDate()
    {
        var seed = Seed();

        var duplicates = seed
            .GroupBy(r => new { r.FestivalKey, r.Date })
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.FestivalKey}@{g.Key.Date:yyyy-MM-dd}")
            .ToList();

        duplicates.Should().BeEmpty("(FestivalKey, Date) is the unique key — no festival may be seeded twice on the same day");
    }

    [Fact]
    public void Seed_HasNoDuplicateIds()
    {
        var seed = Seed();
        seed.Select(r => r.Id).Should().OnlyHaveUniqueItems("HasData requires unique fixed GUIDs");
    }

    [Fact]
    public void Avurudu_IsThePair_Apr13AndApr14_EveryYear()
    {
        var seed = Seed();

        for (var y = FirstYear; y <= LastYear; y++)
        {
            var avurudu = seed.Where(r => r.FestivalKey == "AVURUDU" && r.Date.Year == y).ToList();

            avurudu.Should().HaveCount(2, $"Avurudu {y} is the Apr 13 + Apr 14 pair");
            avurudu.Should().Contain(r => r.Date == new DateTime(y, 4, 13), $"Avurudu {y} must pin Apr 13");
            avurudu.Should().Contain(r => r.Date == new DateTime(y, 4, 14), $"Avurudu {y} must pin Apr 14");

            // Window anchored on Apr 13 (LeadUpDays=14); Apr 14 carries 0 so it is not double-counted.
            var apr13 = avurudu.Single(r => r.Date == new DateTime(y, 4, 13));
            var apr14 = avurudu.Single(r => r.Date == new DateTime(y, 4, 14));
            apr13.LeadUpDays.Should().Be(14, "the lead-up window anchors on the Apr 13 row");
            apr14.LeadUpDays.Should().Be(0, "the paired Apr 14 row must not double-count the window");
        }
    }

    [Fact]
    public void Christmas_IsDec25_EveryYear_AndNeverProvisional()
    {
        var seed = Seed();

        for (var y = FirstYear; y <= LastYear; y++)
        {
            var xmas = seed.Where(r => r.FestivalKey == "CHRISTMAS" && r.Date.Year == y).ToList();
            xmas.Should().ContainSingle($"Christmas {y} is a single fixed-date row");
            xmas[0].Date.Should().Be(new DateTime(y, 12, 25));
            xmas[0].IsProvisional.Should().BeFalse("Christmas is a fixed Gregorian date — zero risk, never provisional");
        }
    }

    [Fact]
    public void ThaiPongal_IsJan14_EveryYear()
    {
        var seed = Seed();

        for (var y = FirstYear; y <= LastYear; y++)
        {
            var pongal = seed.Where(r => r.FestivalKey == "THAI_PONGAL" && r.Date.Year == y).ToList();
            pongal.Should().ContainSingle($"Thai Pongal {y} is a single row");
            pongal[0].Date.Should().Be(new DateTime(y, 1, 14));
        }
    }

    [Fact]
    public void IsProvisional_IsTrue_OnlyForYearsAfterThePinnedReferenceYear()
    {
        var seed = Seed();

        // Christmas is exempt (fixed date, always confirmed); the movable festivals carry the
        // provisional boundary. Assert against the PINNED reference year, never wall-clock.
        var movable = seed.Where(r => r.FestivalKey != "CHRISTMAS").ToList();

        movable.Where(r => r.Date.Year <= LastConfirmedYear)
            .Should().OnlyContain(r => r.IsProvisional == false,
                $"years through {LastConfirmedYear} are gazette-confirmed");

        movable.Where(r => r.Date.Year > LastConfirmedYear)
            .Should().OnlyContain(r => r.IsProvisional,
                $"years after {LastConfirmedYear} are not yet gazette-confirmed → provisional");
    }

    [Fact]
    public void ConfirmedRows_CarryASource_ProvisionalRows_DoNot()
    {
        var seed = Seed();

        // Confirmed movable rows must cite the gazette; provisional movable rows have no citation
        // yet (Source filled in at the annual gazette check). Christmas always has a source.
        seed.Where(r => !r.IsProvisional)
            .Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Source),
                "a confirmed festival date must carry its provenance citation");

        seed.Where(r => r.IsProvisional)
            .Should().OnlyContain(r => r.Source == null,
                "a provisional (uncited) festival date must not fabricate a gazette citation");
    }

    [Fact]
    public void AllRows_HaveDateOnly_NoHiddenTimeComponent()
    {
        var seed = Seed();

        seed.Should().OnlyContain(r => r.Date.TimeOfDay == TimeSpan.Zero,
            "Date is the point-in-time as-of key and must carry no hidden time component");
    }

    [Fact]
    public void AllRows_ShareTheFixedSeedTimestamp_NoWallClock()
    {
        var seed = Seed();

        // A fixed CreatedAtUtc keeps the migrations diff stable (a UtcNow would churn every build).
        seed.Select(r => r.CreatedAtUtc).Distinct().Should().ContainSingle("the seed timestamp is fixed, never UtcNow");
    }
}
