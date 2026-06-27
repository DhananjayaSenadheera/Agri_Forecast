using System.Globalization;
using System.Text.Json;
using AgriForecast.Infrastructure.ExternalSources.DTOs;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.ExternalSources.Interfaces;

// open.er-api.com (free, keyless, LATEST-ONLY). GET v6/latest/USD ->
// { result:"success", rates:{ LKR: number, ... }, time_last_update_utc:"..." }.
public sealed class OpenErApiClient : IEconomicDataClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OpenErApiClient> _logger;

    public OpenErApiClient(HttpClient httpClient, ILogger<OpenErApiClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<FxRate?> GetLatestUsdToLkrAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _httpClient.GetAsync("v6/latest/USD", ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                var preview = body.Length > 300 ? body[..300] : body;
                _logger.LogWarning("open.er-api request failed ({Status}). {Preview}", (int)resp.StatusCode, preview);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            if (!root.TryGetProperty("result", out var result) ||
                !string.Equals(result.GetString(), "success", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("open.er-api returned a non-success result payload.");
                return null;
            }

            // Guard against a misconfigured base: we request USD and read LKR, so the
            // response base must be USD or the rate means something else entirely.
            if (root.TryGetProperty("base_code", out var baseCode) &&
                !string.Equals(baseCode.GetString(), "USD", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("open.er-api base_code was '{Base}', expected USD — skipping.", baseCode.GetString());
                return null;
            }

            if (!root.TryGetProperty("rates", out var rates) ||
                !rates.TryGetProperty("LKR", out var lkr) ||
                lkr.ValueKind != JsonValueKind.Number)
            {
                _logger.LogWarning("open.er-api response did not contain an LKR rate.");
                return null;
            }

            var rate = lkr.GetDecimal();

            // Plausibility guard: USD/LKR has historically sat well within this band.
            // A provider glitch (inverted/zeroed/garbage rate) must not poison the feature.
            if (rate < 200m || rate > 700m)
            {
                _logger.LogWarning("open.er-api USD/LKR rate {Rate} is outside the plausible band [200,700] — skipping.", rate);
                return null;
            }

            // Prefer the provider's rate date; fall back to today (UTC) if unparseable.
            var rateDate = DateOnly.FromDateTime(DateTime.UtcNow);
            if (root.TryGetProperty("time_last_update_utc", out var updated) &&
                DateTimeOffset.TryParse(updated.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                rateDate = DateOnly.FromDateTime(parsed.UtcDateTime);
            }

            return new FxRate(rateDate, rate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Economic data fetch from open.er-api failed.");
            return null;
        }
    }
}
