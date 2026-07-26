using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Admin.Ingestion.Common;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Constants;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.Ingestion.Queries.GetIngestionRuns;

// Server-paged run history: canonicalize the source casing, page via the store, then join each run's
// latest verification by BatchId. The DB is behind IIngestionReadStore for unit-testability.
public class GetIngestionRunsQueryHandler
    : IRequestHandler<GetIngestionRunsQuery, Result<IngestionRunsPage_GetDto>>
{
    private readonly IIngestionReadStore _store;

    public GetIngestionRunsQueryHandler(IIngestionReadStore store) => _store = store;

    public async Task<Result<IngestionRunsPage_GetDto>> Handle(
        GetIngestionRunsQuery request, CancellationToken cancellationToken)
    {
        // Normalize a case-insensitive source to its canonical stored key (null when blank).
        var source = string.IsNullOrWhiteSpace(request.Source)
            ? null
            : IngestionSources.Canonicalize(request.Source);

        var page = await _store.GetRunsPageAsync(request.Page, request.PageSize, source, cancellationToken);

        var batchIds = page.Items.Select(i => i.BatchId).Distinct().ToList();
        var verificationsByBatch =
            await _store.GetLatestVerificationsByBatchAsync(batchIds, cancellationToken);

        var items = page.Items
            .Select(r => new IngestionRun_GetDto
            {
                Id = r.Id,
                BatchId = r.BatchId,
                Source = r.Source,
                StartedUtc = AsUtc(r.StartedUtc),
                FinishedUtc = AsUtc(r.FinishedUtc),
                Status = IngestionStatusStrings.ToWire(r.Status),
                CoveredFromDate = r.CoveredFromDate.HasValue ? Fmt(r.CoveredFromDate.Value) : null,
                CoveredToDate = r.CoveredToDate.HasValue ? Fmt(r.CoveredToDate.Value) : null,
                RowsFetched = r.RowsFetched,
                RowsInserted = r.RowsInserted,
                RowsSkipped = r.RowsSkipped,
                DistinctCrops = r.DistinctCrops,
                ErrorSummary = r.ErrorSummary,
                Verification = verificationsByBatch.TryGetValue(r.BatchId, out var v)
                    ? new IngestionRunVerification_GetDto
                    {
                        OverallStatus = IngestionStatusStrings.ToWire(v.OverallStatus),
                        RanAtUtc = AsUtc(v.RunUtc),
                        NChecksPass = v.NChecksPass,
                        NChecksWarn = v.NChecksWarn,
                        NChecksFail = v.NChecksFail,
                        ChecksJson = v.ChecksJson
                    }
                    : null
            })
            .ToList();

        var dto = new IngestionRunsPage_GetDto
        {
            Items = items,
            Page = request.Page,
            PageSize = request.PageSize,
            Total = page.Total
        };

        return Result<IngestionRunsPage_GetDto>.Success(dto);
    }

    private static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // EF materializes datetime2 as DateTimeKind.Unspecified, so JSON would omit the trailing "Z" and the
    // FE would read these UTC instants as local. Stamp Kind=Utc here for this endpoint.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? AsUtc(DateTime? value) =>
        value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
}
