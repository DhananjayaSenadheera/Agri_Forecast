using AgriForecast.Domain.Enums;
using AgriForecast.Domain.Interfaces;
using AgriForecast.Infrastructure.ExternalSources.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.Services.CbslIngestion;

// CBSL Daily Price Report ingestion (R1.1 P1 Step 6) — a properly-wired SKELETON that is
// DISABLED, not faked.
//
// State of play: nothing CBSL existed in the repo before this step. The service, typed client,
// DTO, DI registration and Worker wiring are all real and match house style. What is NOT real
// is the PDF parse: the CBSL report is a new, un-probed format and (per the single-source-of-
// truth rule the HARTI parser follows) the parser belongs on the Python side. Rather than ship
// a guessing parser, this service is feature-flagged OFF by default.
//
// WATERMARK CONTRACT (why Disabled matters): a Disabled source is a NO-OP and, critically, is
// NEVER counted as a source failure. This is deliberate — a source we chose not to run must not
// pollute the fail-isolation signal or trip alerting. The watermark row is created/kept in the
// Disabled state with the reason recorded, so ops can see at a glance that CBSL is intentionally
// off (distinct from "failing").
//
// Enabling later: set MarketPriceSources:Cbsl:Enabled = true once a Python CBSL parser +
// /admin/ingest-cbsl route exist; the service will then call the parse path, which currently
// throws NotSupportedException loudly (never a silent empty pass).
public class CbslPriceReportIngestionService : ICbslPriceReportIngestionService
{
    public const string SourceKey = "CBSL";

    private const string EnabledConfigKey = "MarketPriceSources:Cbsl:Enabled";
    private const string DisabledReason =
        "CBSL parser not implemented (Python-owned, PDF format un-probed) — feature-flagged OFF.";

    private readonly ICbslPriceReportClient _client;
    private readonly IConfiguration _configuration;
    private readonly IIngestionWatermarkRepository _watermarks;
    private readonly ILogger<CbslPriceReportIngestionService> _logger;

    public CbslPriceReportIngestionService(
        ICbslPriceReportClient client,
        IConfiguration configuration,
        IIngestionWatermarkRepository watermarks,
        ILogger<CbslPriceReportIngestionService> logger)
    {
        _client = client;
        _configuration = configuration;
        _watermarks = watermarks;
        _logger = logger;
    }

    public async Task IngestAsync(CancellationToken ct)
    {
        var enabled = _configuration.GetValue<bool>(EnabledConfigKey);

        // Default (and current) path: feature-flagged OFF. Ensure the watermark reflects the
        // Disabled state (create it Disabled if absent, or move it to Disabled if it was left in
        // some other state) and return — a no-op that is explicitly NOT a source failure.
        if (!enabled)
        {
            var watermark = await _watermarks.GetOrCreateAsync(
                SourceKey, IngestionSourceStatus.Disabled, DisabledReason, ct);

            if (watermark.Status != IngestionSourceStatus.Disabled)
            {
                watermark.Disable(DisabledReason);
                await _watermarks.SaveChangesAsync(ct);
            }

            _logger.LogInformation(
                "CBSL ingestion: DISABLED (feature flag {Flag} is off) — skipping. {Reason}",
                EnabledConfigKey, DisabledReason);
            return;
        }

        // Enabled path (only reachable once a Python CBSL parser + route exist). Reads the
        // resume watermark, then calls the client — which currently throws NotSupportedException
        // loudly. We do NOT swallow that into a fake success: it surfaces to the Worker's
        // per-source try/catch as an ERROR, exactly like any other broken source.
        var wm = await _watermarks.GetOrCreateAsync(SourceKey, ct: ct);
        _logger.LogInformation(
            "CBSL ingestion: ENABLED — fetching report(s) after {Since}.", wm.LastObservedDate);

        var rows = await _client.GetDailyPriceReportAsync(wm.LastObservedDate, ct);

        // Upsert-into-PriceObservations wiring intentionally lives on the Python loader side (as
        // with HARTI), so nothing to persist here yet; the enabled path exists to prove the seam
        // and will be completed alongside the Python parser.
        _logger.LogInformation("CBSL ingestion: parsed {Count} row(s).", rows.Count);

        wm.RecordSuccess(DateTime.UtcNow, message: $"parsed={rows.Count}");
        await _watermarks.SaveChangesAsync(ct);
    }
}
