namespace AgriForecast.Application.Requests.Prices;

/// <summary>
/// The ONE definition of "the single price a farmer sees for one observation row".
/// <para>
/// Extracted verbatim from GetMarketOverviewQueryHandler so the portfolio dashboard shows the same number
/// the market overview shows for the same row. Two copies of this precedence would drift, and a farmer
/// reading Rs 185 on one screen and Rs 190 on another for the same crop on the same day has no way to tell
/// which one is real.
/// </para>
/// </summary>
public static class ObservedUnitPrice
{
    /// <summary>
    /// Precedence: both Min and Max &gt; 0 -&gt; the midpoint of the day's band; else Wholesale; else Retail;
    /// else whichever single bound is &gt; 0. Null when the row carries no usable price at all, and the
    /// caller must then skip the row rather than substitute anything.
    /// </summary>
    /// <remarks>
    /// 0 means ABSENT, not free. The stores that feed this coalesce the nullable price columns to 0, so a
    /// column the source never published and a column published as zero are indistinguishable here — which
    /// is the safe way round: a zero price is never rendered as a real one.
    /// </remarks>
    public static decimal? From(decimal min, decimal max, decimal wholesale, decimal retail)
    {
        if (min > 0m && max > 0m) return (min + max) / 2m;
        if (wholesale > 0m) return wholesale;
        if (retail > 0m) return retail;
        if (max > 0m) return max;
        if (min > 0m) return min;
        return null;
    }
}
