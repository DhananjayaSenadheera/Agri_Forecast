using System.Globalization;

namespace AgriForecast.Application.Requests.Portfolio.Common;

/// <summary>
/// The one place a sales-log <c>UserActivityLog.Details</c> note is worded:
/// <c>"&lt;CropCode&gt; · Rs &lt;price&gt;/kg · &lt;yyyy-MM-dd&gt;"</c>, e.g.
/// <c>"VEG000041 · Rs 155.00/kg · 2026-07-28"</c>.
/// </summary>
/// <remarks>
/// THE FARMER'S NOTE IS NOT A PARAMETER, and that is the enforcement — the same mechanism
/// <see cref="PlantedDateRemovalReasons.RenderAuditDetails"/> uses. Audit Details is code-authored text
/// only; the free-text note lives solely on the UserSales row, where it is scoped to its owner. A renderer
/// that COULD accept the note would leak it the first time somebody at a call site passed the wrong local
/// variable, so it takes a crop code, a price and a date and nothing else. Widening this signature is the
/// change the privacy test in UserSaleTests exists to fail.
/// <para>
/// The quantity is not here either, for a smaller reason: it is optional, so half the rows would render a
/// ragged note, and how much a farmer sold is nobody's business in an admin log.
/// </para>
/// <para>
/// Invariant formatting throughout: the price is always two decimals with a dot, and the date always
/// yyyy-MM-dd, so an admin's server locale can never re-punctuate a stored audit line.
/// </para>
/// </remarks>
public static class SaleAuditDetails
{
    /// <summary>
    /// Renders the note. A blank crop code drops the code and its separator rather than emitting a leading
    /// " · " — the code comes from a post-commit read-back, which can (rarely) fail, and a note that starts
    /// with a separator would look like a rendering bug instead of a missing lookup.
    /// </summary>
    public static string RenderAuditDetails(string? cropCode, decimal pricePerKg, DateOnly saleDate)
    {
        var money = "Rs " + pricePerKg.ToString("0.00", CultureInfo.InvariantCulture) + "/kg";
        var day = PortfolioTime.Fmt(saleDate);

        return string.IsNullOrWhiteSpace(cropCode)
            ? $"{money} · {day}"
            : $"{cropCode.Trim()} · {money} · {day}";
    }
}
