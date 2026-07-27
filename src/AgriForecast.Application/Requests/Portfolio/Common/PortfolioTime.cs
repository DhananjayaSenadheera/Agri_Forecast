using System.Globalization;

namespace AgriForecast.Application.Requests.Portfolio.Common;

// Date/time formatting shared by the portfolio handlers, matching the admin accuracy handlers exactly so
// the two surfaces cannot render the same instant differently.
internal static class PortfolioTime
{
    // EF materializes datetime2 as DateTimeKind.Unspecified, so JSON would omit the trailing "Z" and the
    // FE would read these UTC instants as local — a 5.5-hour error in Sri Lanka.
    public static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // Date-only columns go out as yyyy-MM-dd, invariant, so the UI's ymdLocal parsing never sees a
    // timezone to misinterpret.
    public static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? Fmt(DateOnly? d) => d.HasValue ? Fmt(d.Value) : null;
}
