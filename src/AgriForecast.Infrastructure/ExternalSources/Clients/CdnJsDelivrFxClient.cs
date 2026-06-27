using System.Text.Json;
using AgriForecast.Infrastructure.ExternalSources.DTOs;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.ExternalSources.Interfaces;

// Historical USD/LKR rates via fawazahmed0/currency-api (jsDelivr CDN).
// Free, keyless, daily history since 2000.
// URL: https://cdn.jsdelivr.net/gh/fawazahmed0/currency-api@{date}/v1/currencies/usd/lkr.json
// Response: { "date": "2025-01-01", "lkr": 294.5 }
public sealed class CdnJsDelivrFxClient : IFxHistoricalClient
{
    private const decimal MinPlausibleRate = 200m;
    private const decimal MaxPlausibleRate = 700m;

    private readonly HttpClient _http;
    private readonly ILogger<CdnJsDelivrFxClient> _logger;

    public CdnJsDelivrFxClient(HttpClient http, ILogger<CdnJsDelivrFxClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<IReadOnlyList<FxRate>> GetRatesForMonthStartsAsync(
        IReadOnlyList<DateOnly> monthStarts, CancellationToken ct)
    {
        var results = new List<FxRate>(monthStarts.Count);
        foreach (var monthStart in monthStarts)
        {
            if (ct.IsCancellationRequested) break;

            var rate = await FetchWithFallbackAsync(monthStart, ct);
            if (rate is not null)
                results.Add(rate);

            // Small delay to be polite to the CDN.
            await Task.Delay(150, ct).ConfigureAwait(false);
        }
        return results;
    }

    // Tries the requested date then up to 4 subsequent days in case CDN has no data for that exact date.
    private async Task<FxRate?> FetchWithFallbackAsync(DateOnly date, CancellationToken ct)
    {
        for (int offset = 0; offset < 5; offset++)
        {
            var d = date.AddDays(offset);
            var rate = await TryFetchAsync(d, ct);
            if (rate is not null)
                return rate;
        }
        _logger.LogWarning("FX backfill: no USD/LKR rate found near {Date:yyyy-MM-dd} (tried 5 days).", date);
        return null;
    }

    private async Task<FxRate?> TryFetchAsync(DateOnly date, CancellationToken ct)
    {
        var path = $"gh/fawazahmed0/currency-api@{date:yyyy-MM-dd}/v1/currencies/usd/lkr.json";
        try
        {
            using var resp = await _http.GetAsync(path, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("lkr", out var lkr) ||
                lkr.ValueKind != JsonValueKind.Number)
                return null;

            var value = lkr.GetDecimal();
            if (value < MinPlausibleRate || value > MaxPlausibleRate)
            {
                _logger.LogWarning(
                    "FX backfill: USD/LKR {Rate} for {Date:yyyy-MM-dd} outside [{Min},{Max}] — skipped.",
                    value, date, MinPlausibleRate, MaxPlausibleRate);
                return null;
            }

            return new FxRate(date, value);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FX backfill: fetch failed for {Date:yyyy-MM-dd}.", date);
            return null;
        }
    }
}
