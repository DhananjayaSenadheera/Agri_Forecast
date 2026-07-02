using AgriForecast.Infrastructure.ExternalSources.DTOs;

namespace AgriForecast.Infrastructure.ExternalSources.Interfaces;

// Typed client for the CBSL Daily Price Report source (R1.1 P1 Step 6 skeleton).
//
// SCOPE: the CBSL report is a PDF in a new, un-probed format. Per the single-source-of-truth
// rule that the HARTI parser follows, PDF parsing belongs on the Python side — it is NOT
// implemented in C#. This client therefore exposes the point-in-time contract and the fetch
// seam, but its parse path throws NotSupportedException until a Python CBSL parser exists. See
// CbslPriceReportIngestionService, which keeps the source in the DISABLED watermark state so
// this never counts as a source failure.
public interface ICbslPriceReportClient
{
    // Fetch + parse the CBSL daily price report published strictly AFTER sinceDate (null = the
    // latest available). Returns parsed rows carrying their own PublishedAtUtc vintage.
    //
    // NOT IMPLEMENTED: throws NotSupportedException (loud, never a silent empty list) until the
    // Python CBSL parser lands. A silent empty list would masquerade as "no new data" and let a
    // broken source look healthy — exactly the failure mode this method refuses.
    Task<IReadOnlyList<CbslDailyPriceReportDto>> GetDailyPriceReportAsync(
        DateOnly? sinceDate,
        CancellationToken ct);
}
