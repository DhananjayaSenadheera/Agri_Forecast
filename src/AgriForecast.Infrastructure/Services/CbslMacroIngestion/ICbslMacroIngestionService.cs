using AgriForecast.Application.common;

namespace AgriForecast.Infrastructure.Services.CbslMacroIngestion;

public interface ICbslMacroIngestionService
{
    // Returns run-tracking stats so the pass runner can put an HONEST status on the run row, mirroring
    // ICbslPriceReportIngestionService: the feature-flag-off and watermark-Disabled paths are deliberate
    // no-ops reported as Skipped, while a transport, HTTP or parse failure is Failed with the same reason
    // the watermark got — never a green Succeeded row.
    Task<IngestionRunStats> IngestAsync(CancellationToken ct);
}
