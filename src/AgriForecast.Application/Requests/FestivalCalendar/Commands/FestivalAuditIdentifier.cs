using System.Globalization;

namespace AgriForecast.Application.Requests.FestivalCalendar.Commands;

// The short handle a festival row is identified by on its audit note, e.g. "VESAK 2027-05-10".
// Both parts are needed: the key alone is ambiguous because a festival recurs every year, and together
// they are exactly the DB's UNIQUE (FestivalKey, Date) key.
// InvariantCulture is explicit: under a non-Gregorian default calendar a culture-sensitive "yyyy" would
// render a different year, so the audit note would disagree with the row it describes.
internal static class FestivalAuditIdentifier
{
    public static string For(string? festivalKey, DateTime date) =>
        $"{festivalKey} {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}".Trim();
}
