using System.Globalization;
using AgriForecast.Application.Services;
using Microsoft.Extensions.Configuration;

namespace AgriForecast.Infrastructure.Services.PipelineHealth;

// Resolves the daily pipeline's schedule from configuration, applying the documented fallbacks, so the
// Application layer takes no dependency on IConfiguration or on a time-zone lookup that can throw.
// Defaults mirror k8s/pipeline-daily.yaml: "0 21 * * *", timeZone Asia/Colombo, 21600s catch-up.
public class PipelineScheduleSettings : IPipelineScheduleSettings
{
    private const string TimeZoneKey = "PipelineSchedule:TimeZone";
    private const string FireTimeKey = "PipelineSchedule:LocalFireTime";
    private const string CatchUpKey = "PipelineSchedule:CatchUpWindowMinutes";

    private const string DefaultTimeZoneId = "Asia/Colombo";
    private const int DefaultCatchUpWindowMinutes = 360; // startingDeadlineSeconds 21600 / 60
    private static readonly TimeOnly DefaultFireTime = new(21, 0);

    // Sri Lanka has been a fixed UTC+05:30 since 2006 and observes no DST, so this is exact — it is a
    // last-resort stand-in for a container image shipped without tzdata, not an approximation.
    private static readonly TimeSpan ColomboOffset = TimeSpan.FromMinutes(330);

    public TimeZoneInfo ScheduleTimeZone { get; }
    public TimeOnly LocalFireTime { get; }
    public int CatchUpWindowMinutes { get; }

    public PipelineScheduleSettings(IConfiguration configuration)
    {
        var zoneId = configuration[TimeZoneKey];
        ScheduleTimeZone = ResolveTimeZone(
            string.IsNullOrWhiteSpace(zoneId) ? DefaultTimeZoneId : zoneId);

        // Read the raw string and TryParse rather than GetValue<T>: a malformed value must not throw at
        // construction, because that is a 500 on the very endpoint whose job is to report trouble.
        var rawFireTime = configuration[FireTimeKey];
        LocalFireTime = TimeOnly.TryParseExact(
            rawFireTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fireTime)
            ? fireTime
            : DefaultFireTime;

        var rawCatchUp = configuration[CatchUpKey];
        CatchUpWindowMinutes = int.TryParse(rawCatchUp, NumberStyles.Integer, CultureInfo.InvariantCulture,
            out var catchUp) && catchUp > 0
            ? catchUp
            : DefaultCatchUpWindowMinutes;
    }

    // IANA id first (Linux containers and .NET 6+ on Windows), then the Windows/IANA conversions for a
    // host whose tz database only knows one of the two spellings, then a fixed-offset stand-in. Never
    // throws: a health endpoint that dies on a missing time-zone file cannot report anything.
    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        var direct = TryFind(id);
        if (direct is not null) return direct;

        if (TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out var windowsId) && windowsId is not null)
        {
            var converted = TryFind(windowsId);
            if (converted is not null) return converted;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var ianaId) && ianaId is not null)
        {
            var converted = TryFind(ianaId);
            if (converted is not null) return converted;
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "Asia/Colombo (fixed +05:30)", ColomboOffset, "Sri Lanka Time", "Sri Lanka Time");
    }

    private static TimeZoneInfo? TryFind(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }
}
