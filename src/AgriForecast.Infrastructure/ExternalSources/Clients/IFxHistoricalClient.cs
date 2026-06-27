using AgriForecast.Infrastructure.ExternalSources.DTOs;

namespace AgriForecast.Infrastructure.ExternalSources.Interfaces;

public interface IFxHistoricalClient
{
    // Returns USD/LKR rates for each requested month-start date (1st of month).
    // Months with no data available from the provider are silently omitted.
    Task<IReadOnlyList<FxRate>> GetRatesForMonthStartsAsync(IReadOnlyList<DateOnly> monthStarts, CancellationToken ct);
}
