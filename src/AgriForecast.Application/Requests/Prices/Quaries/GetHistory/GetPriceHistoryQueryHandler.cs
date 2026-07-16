using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Prices.DTOs;
using AgriForecast.Application.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Application.Requests.Prices.Quaries.GetHistory;

// Observed daily low-high price series for one crop (optionally one market). Owns the
// window clamp + fence-post and the per-date aggregation; the DB is behind
// IPriceHistoryStore so this is unit-testable.
//
// Envelope semantics: per calendar date, MinPrice = the lowest observed low and
// MaxPrice = the highest observed high across the day's usable rows. When MarketId is
// null this spans all markets (a cross-market national envelope); when set it is that
// one market's band. The store already excludes quarantined / unusable rows, so nothing
// is fabricated here.
//
// Empty result is a valid 200 [] (never a failure) — the Prices page renders the honest
// "no data" chart state.
public class GetPriceHistoryQueryHandler
    : IRequestHandler<GetPriceHistoryQuery, Result<List<PriceHistoryPoint_GetDto>>>
{
    private const int MinDays = 7;
    private const int MaxDays = 365;

    private readonly IPriceHistoryStore _store;
    private readonly ILogger<GetPriceHistoryQueryHandler> _logger;

    public GetPriceHistoryQueryHandler(
        IPriceHistoryStore store, ILogger<GetPriceHistoryQueryHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<Result<List<PriceHistoryPoint_GetDto>>> Handle(
        GetPriceHistoryQuery request, CancellationToken cancellationToken)
    {
        var windowDays = Math.Clamp(request.Days, MinDays, MaxDays);

        var latest = await _store.GetLatestObservedDateAsync(
            request.CropId, request.MarketId, cancellationToken);
        if (latest is null)
        {
            _logger.LogInformation(
                "No usable price history for crop {CropId} (market {MarketId}).",
                request.CropId, request.MarketId);
            return Result<List<PriceHistoryPoint_GetDto>>.Success(new List<PriceHistoryPoint_GetDto>());
        }

        var toInclusive = latest.Value;
        // Inclusive window of exactly windowDays calendar days ending at the latest
        // observation (fence-post: -(windowDays - 1), both ends inclusive).
        var fromInclusive = toInclusive.AddDays(-(windowDays - 1));

        var rows = await _store.GetRowsAsync(
            request.CropId, request.MarketId, fromInclusive, toInclusive, cancellationToken);

        var points = rows
            // Fail-closed guard, defense-in-depth: the store already excludes rows without
            // a usable band, but re-check here so a future/looser store can never let a
            // null/zero bound fabricate a range (0 == "absent" per the store's coalescing).
            .Where(r => r.MinPrice > 0m && r.MaxPrice > 0m)
            .GroupBy(r => r.Date)
            .Select(g => new PriceHistoryPoint_GetDto
            {
                Date = Fmt(g.Key),
                MinPrice = g.Min(r => r.MinPrice),
                MaxPrice = g.Max(r => r.MaxPrice)
            })
            .OrderBy(p => p.Date) // yyyy-MM-dd sorts chronologically as a string
            .ToList();

        return Result<List<PriceHistoryPoint_GetDto>>.Success(points);
    }

    private static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
