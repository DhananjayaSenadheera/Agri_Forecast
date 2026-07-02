using AgriForecast.Infrastructure.ExternalSources.DTOs;
using Microsoft.Extensions.Logging;

namespace AgriForecast.Infrastructure.ExternalSources.Interfaces;

// Typed client skeleton for the CBSL Daily Price Report (R1.1 P1 Step 6).
//
// DELIBERATELY UNFINISHED, HONESTLY. The CBSL report is a PDF in a new format that has NOT been
// corpus-probed, and PDF parsing belongs on the Python side (same rule the HARTI parser follows).
// Shipping a guessing parser here would be worse than shipping nothing — it could silently
// mis-map prices/units. So the fetch+parse entry point throws NotSupportedException (loud), and
// CbslPriceReportIngestionService keeps the source in the DISABLED watermark state so this is a
// no-op, NOT a source failure. What remains to finish it is documented on the service.
public sealed class CbslPriceReportClient : ICbslPriceReportClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<CbslPriceReportClient> _logger;

    public CbslPriceReportClient(HttpClient httpClient, ILogger<CbslPriceReportClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public Task<IReadOnlyList<CbslDailyPriceReportDto>> GetDailyPriceReportAsync(
        DateOnly? sinceDate,
        CancellationToken ct)
    {
        _logger.LogWarning(
            "CBSL daily price report parse is not implemented (feature-flagged OFF). "
            + "The PDF parser is owned by the Python side and has not been built; this client "
            + "will not guess. sinceDate={SinceDate}.",
            sinceDate);

        throw new NotSupportedException(
            "CBSL Daily Price Report parsing is not implemented yet. The PDF is a new, un-probed "
            + "format; a reliable parser belongs on the Python side (single-source-of-truth rule) "
            + "and must not be guessed in C#. This client is a skeleton kept behind the disabled "
            + "CBSL watermark. To finish: (1) corpus-probe the CBSL PDF layout, (2) add a Python "
            + "parser + loader (upsert into PriceObservations with WholesalePrice/RetailPrice, "
            + "Source='CBSL', AsOfUtc = report PublishedAt), (3) expose an /admin/ingest-cbsl "
            + "FastAPI route, (4) point this service at it and flip the watermark from Disabled to Ok.");
    }
}
