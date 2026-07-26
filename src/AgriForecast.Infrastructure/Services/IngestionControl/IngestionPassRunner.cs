using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.Services.CbslIngestion;
using AgriForecast.Infrastructure.Services.CbslMacroIngestion;
using AgriForecast.Infrastructure.Services.EconomicIngestion;
using AgriForecast.Infrastructure.Services.HartiIngestion;
using AgriForecast.Infrastructure.Services.MarketPriceIngestion;
using AgriForecast.Infrastructure.Services.NewsIngestion;
using AgriForecast.Infrastructure.Services.WeatherIngestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.IngestionControl;

// The single ingestion pass, lifted verbatim out of the Ingestion Worker so the Worker and the API's
// admin start button share ONE source sequence instead of two copies that drift.
//
// Scoping: the runner self-scopes from IServiceScopeFactory rather than taking scoped services in its
// constructor. That is load-bearing for the API host — a pass is started from a request and outlives it,
// so resolving the sources from the request scope would hand the background task a DbContext that is
// disposed the moment the 202 is written. It is registered SINGLETON for the same reason.
//
// Every source is wrapped by IngestionRunAudit: a Running row is committed before the source runs, then
// transitioned to Succeeded with counts, or Failed with a sanitized error. The wrapper also catches the
// source's exception — it is the per-source fail-isolation belt — and an audit write can never break the
// pass.
public class IngestionPassRunner : IIngestionPassRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IngestionPassRunner> _logger;

    public IngestionPassRunner(IServiceScopeFactory scopeFactory, ILogger<IngestionPassRunner> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task RunPassAsync(Guid batchId, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var runs = scope.ServiceProvider.GetRequiredService<IIngestionRunRepository>();

        _logger.LogInformation("Ingestion pass starting. BatchId={BatchId}", batchId);

        // Sources run in this fixed order. Each entry is (source key, body); the loop below applies the
        // audit wrapper and the between-sources cancellation check uniformly, so no source can be added
        // without both.
        var sources = new (string Source, Func<CancellationToken, Task<IngestionRunStats?>> Body)[]
        {
            // DAMBULLA_DEC market prices (reports counts).
            (IngestionSources.DambullaDec, async innerCt =>
            {
                var ingestion = scope.ServiceProvider.GetRequiredService<IMarketPriceIngestionService>();
                _logger.LogInformation("Market price ingestion started");
                var stats = await ingestion.IngestAsync(innerCt);
                _logger.LogInformation("Market price ingestion finished");
                return stats;
            }),

            // WEATHER (status-only — signature unchanged, so the run row carries null counts).
            (IngestionSources.Weather, async innerCt =>
            {
                var weather = scope.ServiceProvider.GetRequiredService<IWeatherIngestionService>();
                _logger.LogInformation("Weather ingestion started");
                await weather.IngestAsync(innerCt);
                _logger.LogInformation("Weather ingestion finished");
                return (IngestionRunStats?)null;
            }),

            // ECONOMIC (status-only).
            (IngestionSources.Economic, async innerCt =>
            {
                var economic = scope.ServiceProvider.GetRequiredService<IEconomicIngestionService>();
                _logger.LogInformation("Economic ingestion started");
                await economic.IngestAsync(innerCt);
                _logger.LogInformation("Economic ingestion finished");
                return (IngestionRunStats?)null;
            }),

            // NEWS. The service is fail-safe (it does not throw on a transport or HTTP error) so it reports
            // its own outcome via IngestionRunStats.Outcome — a swallowed error must land as a Failed run
            // row, never as a green one.
            (IngestionSources.News, async innerCt =>
            {
                var news = scope.ServiceProvider.GetRequiredService<INewsIngestionService>();
                _logger.LogInformation("News ingestion started");
                var stats = await news.IngestAsync(innerCt);
                _logger.LogInformation("News ingestion finished");
                return (IngestionRunStats?)stats;
            }),

            // HARTI multi-market bulletin ingestion (reports counts). The service also self-heals its
            // watermark on an internal failure; the audit wrapper is the outer belt.
            (HartiBulletinIngestionService.SourceKey, async innerCt =>
            {
                var harti = scope.ServiceProvider.GetRequiredService<IHartiBulletinIngestionService>();
                _logger.LogInformation("HARTI ingestion started");
                var stats = await harti.IngestAsync(innerCt);
                _logger.LogInformation("HARTI ingestion finished");
                return (IngestionRunStats?)stats;
            }),

            // CBSL Daily Price Report ingestion (capture-only): the service orchestrates the Python parser
            // via /admin/ingest-cbsl and reports counts. MarketPriceSources:Cbsl:Enabled is the pause
            // switch — flag off leaves a Disabled watermark, a deliberate no-op reported as Skipped, never
            // a source failure.
            (CbslPriceReportIngestionService.SourceKey, async innerCt =>
            {
                var cbsl = scope.ServiceProvider.GetRequiredService<ICbslPriceReportIngestionService>();
                _logger.LogInformation("CBSL ingestion started");
                var stats = await cbsl.IngestAsync(innerCt);
                _logger.LogInformation("CBSL ingestion finished");
                return (IngestionRunStats?)stats;
            }),

            // CBSL macro (CCPI/MEI vintage) ingestion, feature-flagged off by default: a Disabled gating
            // watermark is a deliberate no-op, not a source failure. Status-only run row.
            (CbslMacroIngestionService.SourceKey, async innerCt =>
            {
                var cbslMacro = scope.ServiceProvider.GetRequiredService<ICbslMacroIngestionService>();
                _logger.LogInformation("CBSL macro ingestion started");
                await cbslMacro.IngestAsync(innerCt);
                _logger.LogInformation("CBSL macro ingestion finished");
                return (IngestionRunStats?)null;
            })
        };

        foreach (var (source, body) in sources)
        {
            // Admin stop (or host shutdown) between sources: stop launching new work rather than tearing
            // a source down mid-write. The in-flight source gets the same token and may end sooner.
            if (ct.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "Ingestion pass {BatchId} cancelled before {Source}; remaining sources skipped.",
                    batchId, source);
                return;
            }

            await IngestionRunAudit.RunTrackedAsync(runs, _logger, batchId, source, body, ct);
        }

        _logger.LogInformation("Ingestion pass complete. BatchId={BatchId}", batchId);
    }
}
