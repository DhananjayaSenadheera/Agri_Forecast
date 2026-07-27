namespace AgriForecast.Application.Requests.Admin.Pipeline;

// Wall-clock maths for the nightly pipeline schedule, shared by everything that has to answer "which
// night are we talking about?" — the health query handler and the email sentinel.
//
// It lives in one place on purpose. The health endpoint and the sentinel MUST agree on the fire time and
// on how a DST edge is resolved, or the sentinel would report on a different night than the banner does.
// Every method is total: none of them throws on an invalid or ambiguous local time, because the callers
// are a status endpoint and an alerting loop, and both are worse than useless if they die on a clock
// change.
public static class PipelineScheduleClock
{
    // The most recent scheduled fire time that has already passed, as both a UTC instant and the local
    // (Asia/Colombo) date that names the night. Computed from the zone rather than from UTC arithmetic:
    // 21:00 Colombo is 15:30 UTC, so between 18:30 UTC and midnight UTC the Colombo date has already
    // rolled over while the fire being reported on is still yesterday's.
    public static (DateTime Utc, DateOnly LocalDate) ResolveMostRecentFire(
        DateTime nowUtc, TimeZoneInfo tz, TimeOnly localFireTime)
    {
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);

        var localDate = DateOnly.FromDateTime(nowLocal);
        if (localDate.ToDateTime(localFireTime) > nowLocal)
            localDate = localDate.AddDays(-1);

        return (ToUtcInstant(localDate.ToDateTime(localFireTime), tz), localDate);
    }

    // When a night ends: the next scheduled fire after the one given. Recomputed through the zone rather
    // than by adding 24h, for the same DST reason as above.
    public static DateTime ResolveNextFire(
        (DateTime Utc, DateOnly LocalDate) fire, TimeZoneInfo tz, TimeOnly localFireTime) =>
        ToUtcInstant(fire.LocalDate.AddDays(1).ToDateTime(localFireTime), tz);

    // The next instant at which the local wall clock reads localTime, STRICTLY after nowUtc. Used for the
    // sentinel's daily check time and for "when does the next pipeline fire?" — the deadline it stops
    // waiting on a still-running night at.
    //
    // Strictly-after matters: an "at or after" comparison would return the current instant when called
    // exactly on the check time, and the sentinel's loop would spin instead of sleeping a day.
    public static DateTime ResolveNextOccurrenceUtc(DateTime nowUtc, TimeZoneInfo tz, TimeOnly localTime)
    {
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(
            DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), tz);
        var localDate = DateOnly.FromDateTime(nowLocal);

        // Today, then tomorrow. The third day is unreachable arithmetic insurance: it would take an
        // offset shift of more than 24h for two consecutive local occurrences to both land in the past.
        for (var dayOffset = 0; dayOffset <= 2; dayOffset++)
        {
            var candidate = ToUtcInstant(localDate.AddDays(dayOffset).ToDateTime(localTime), tz);
            if (candidate > nowUtc)
                return candidate;
        }

        return nowUtc.AddDays(1);
    }

    // Wall clock -> UTC, without throwing on a DST transition. Sri Lanka has no DST so neither branch
    // fires today, but the zone is configurable and a health endpoint that 500s on a clock change would
    // be worse than useless.
    public static DateTime ToUtcInstant(DateTime localWallClock, TimeZoneInfo tz)
    {
        var unspecified = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);

        // Skipped by a spring-forward: no such instant exists, so take the offset in effect just before
        // the transition, which lands on the first real instant at or after the intended one.
        if (tz.IsInvalidTime(unspecified))
            return unspecified - tz.GetUtcOffset(unspecified);

        // Repeated by a fall-back: take the larger offset, i.e. the FIRST of the two occurrences, so the
        // window opens at the earlier fire rather than an hour late.
        if (tz.IsAmbiguousTime(unspecified))
            return unspecified - tz.GetAmbiguousTimeOffsets(unspecified).Max();

        return TimeZoneInfo.ConvertTimeToUtc(unspecified, tz);
    }
}
