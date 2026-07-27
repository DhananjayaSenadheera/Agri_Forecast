using System.Globalization;
using AgriForecast.Application.common;
using AgriForecast.Application.Services;
using MediatR;

namespace AgriForecast.Application.Requests.Admin.ForecastAccuracy.Queries.GetForecastSnapshots;

// Server-paged snapshot ledger. Normalizes the optional filters, pages via IForecastAccuracyReadStore
// and maps each row to the DTO by hand — same shape as the other admin list handlers.
public class GetForecastSnapshotsQueryHandler
    : IRequestHandler<GetForecastSnapshotsQuery, Result<ForecastSnapshotsPage_GetDto>>
{
    private readonly IForecastAccuracyReadStore _store;

    public GetForecastSnapshotsQueryHandler(IForecastAccuracyReadStore store) => _store = store;

    public async Task<Result<ForecastSnapshotsPage_GetDto>> Handle(
        GetForecastSnapshotsQuery request, CancellationToken cancellationToken)
    {
        // A blank or whitespace-only ?modelVersion= is NO filter, not a filter on the empty string —
        // otherwise an empty query-string parameter would silently return zero rows.
        var modelVersion = string.IsNullOrWhiteSpace(request.ModelVersion)
            ? null
            : request.ModelVersion.Trim();

        var page = await _store.GetSnapshotsPageAsync(
            request.Page, request.PageSize, request.CropId, modelVersion, request.MaturedOnly,
            cancellationToken);

        var items = page.Items
            .Select(r => new ForecastSnapshot_GetDto
            {
                Id = r.Id,
                CropId = r.CropId,
                CropName = r.CropName,
                CropCode = r.CropCode,
                SnapshotDate = Fmt(r.SnapshotDate),
                HarvestDate = Fmt(r.HarvestDate),
                GrowthPeriodDays = r.GrowthPeriodDays,
                PredictedPrice = r.PredictedPrice,
                LowerBound = r.LowerBound,
                UpperBound = r.UpperBound,
                ReferencePrice = r.ReferencePrice,
                Confidence = r.Confidence,
                ActivePredictor = r.ActivePredictor,
                FallbackTier = r.FallbackTier,
                ModelVersion = r.ModelVersion,
                ReasonCode = r.ReasonCode,
                MaturityState = r.MaturityState,
                ActualPrice = r.ActualPrice,
                ActualObservedDate = Fmt(r.ActualObservedDate),
                SignedError = r.SignedError,
                AbsoluteError = r.AbsoluteError,
                PercentageError = r.PercentageError,
                WithinInterval = r.WithinInterval,
                CreatedAtUtc = AsUtc(r.CreatedAtUtc),
                MaturedAtUtc = AsUtc(r.MaturedAtUtc)
            })
            .ToList();

        var dto = new ForecastSnapshotsPage_GetDto
        {
            Items = items,
            Page = request.Page,
            PageSize = request.PageSize,
            Total = page.Total
        };

        return Result<ForecastSnapshotsPage_GetDto>.Success(dto);
    }

    private static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Fmt(DateOnly? d) => d.HasValue ? Fmt(d.Value) : null;

    // EF materializes datetime2 as DateTimeKind.Unspecified, so JSON would omit the trailing "Z" and the
    // FE would read these UTC instants as local.
    private static DateTime AsUtc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? AsUtc(DateTime? value) =>
        value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
}
