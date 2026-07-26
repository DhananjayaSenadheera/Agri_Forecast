using System.Globalization;

namespace AgriForecast.Application.Requests.FestivalCalendar.Commands;

// The short handle a festival-calendar row is identified by on its content-audit row
// (UserActivityLog Details, e.g. "deleted 'VESAK 2027-05-10'").
//
// A festival needs BOTH parts: FestivalKey alone is ambiguous (the same festival recurs every year —
// per-occurrence rows are the design, there are no recurrence rules), and the date alone says nothing.
// Together they are exactly the DB's UNIQUE (FestivalKey, Date) key, so an audit reader can always
// tell which occurrence was touched.
//
// InvariantCulture is explicit, not decorative: under a non-Gregorian default calendar (ar-SA) a
// culture-sensitive "yyyy" would render a different YEAR, so an audit note would silently disagree
// with the row it describes.
internal static class FestivalAuditIdentifier
{
    public static string For(string? festivalKey, DateTime date) =>
        $"{festivalKey} {date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}".Trim();
}
