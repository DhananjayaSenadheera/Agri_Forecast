using System.Globalization;

namespace AgriForecast.Application.Requests.Portfolio.Common;

// Date/time formatting and parsing shared by the portfolio handlers, matching the admin accuracy handlers
// exactly so the two surfaces cannot render the same instant differently.
//
// PUBLIC rather than internal since the sales log arrived: LatestPlausibleLocalDate is now THE clock every
// "not in the future" rule on this controller reads, and a rule that decides what a farmer may claim is
// worth pinning directly in a test rather than only through a handler that reads DateTime.UtcNow.
public static class PortfolioTime
{
    // EF materializes datetime2 as DateTimeKind.Unspecified, so JSON would omit the trailing "Z" and the
    // FE would read these UTC instants as local — a 5.5-hour error in Sri Lanka.
    public static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    // Date-only columns go out as yyyy-MM-dd, invariant, so the UI's ymdLocal parsing never sees a
    // timezone to misinterpret.
    public static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static string? Fmt(DateOnly? d) => d.HasValue ? Fmt(d.Value) : null;

    /// <summary>
    /// Parses a <c>yyyy-MM-dd</c> wire date, or null when it is blank or spelled any other way. Exactly the
    /// inverse of <see cref="Fmt(DateOnly)"/>, so a value this API emitted always round-trips.
    /// </summary>
    /// <remarks>
    /// Strict and invariant on purpose: "28/07/2026" is a client that has not read the contract, not a
    /// locale to accommodate, and guessing between day-first and month-first would silently move a sale by
    /// months. Surrounding whitespace is tolerated (a transport artifact); nothing else is.
    /// </remarks>
    public static DateOnly? ParseYmd(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        return DateOnly.TryParseExact(
            value.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// The latest calendar day a farmer can honestly claim as "today" — the UTC date PLUS ONE DAY.
    /// </summary>
    /// <remarks>
    /// THE ONE CLOCK for every "not in the future" rule on this controller (planting dates and sale dates
    /// both call it), so the two surfaces cannot disagree about which day it is.
    /// <para>
    /// Sri Lanka is UTC+5:30, so between 18:30 and 24:00 UTC a farmer's local "today" is already the next
    /// calendar day; a strict UTC cutoff would reject the honest answer of anyone recording during their own
    /// evening. One day of slack fixes that without letting a genuine future date through — nobody sells
    /// next week by accident, and the error a farmer actually makes (a mis-keyed year) is caught anyway.
    /// </para>
    /// <para>
    /// The zone is not consulted directly on purpose: TimeZoneInfo.FindSystemTimeZoneById needs a tz
    /// database in the container, and a validation rule that throws when the image ships without tzdata
    /// would be a worse failure than a day of tolerance.
    /// </para>
    /// </remarks>
    public static DateOnly LatestPlausibleLocalDate(DateTime nowUtc) =>
        DateOnly.FromDateTime(nowUtc).AddDays(1);
}
