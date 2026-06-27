namespace AgriForecast.Domain.Entities;

// A single economic data point captured forward-daily (e.g. USD/LKR FX rate).
// Flexible by design: IndicatorCode identifies the series, Value holds the reading.
public class EconomicIndicator
{
    public Guid Id { get; private set; }

    public DateTime Date { get; private set; }        // the rate's effective date (date-only)
    public string IndicatorCode { get; private set; } = null!;   // e.g. "USD_LKR"
    public decimal Value { get; private set; }
    public string Source { get; private set; } = null!;
    public DateTime RetrievedAtUtc { get; private set; }

    private EconomicIndicator() { }

    public EconomicIndicator(
        DateTime date,
        string indicatorCode,
        decimal value,
        string source,
        DateTime retrievedAtUtc)
    {
        if (string.IsNullOrWhiteSpace(indicatorCode))
            throw new ArgumentException("Indicator code is required", nameof(indicatorCode));

        if (value <= 0)
            throw new ArgumentException("Indicator value must be greater than zero", nameof(value));

        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Source is required", nameof(source));

        Date = date.Date;
        IndicatorCode = indicatorCode;
        Value = value;
        Source = source;
        RetrievedAtUtc = retrievedAtUtc;
    }
}
