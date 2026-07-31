using AgriForecast.Application.common;
using MediatR;

namespace AgriForecast.Application.Requests.Portfolio.Queries.GetSales;

/// <summary>
/// GET /api/portfolio/sales?page=&amp;pageSize=&amp;cropId= — the caller's own sales, newest first.
/// </summary>
/// <remarks>
/// Serves BOTH farmer surfaces from one route: the all-sales page (no <c>cropId</c>) and the per-crop list
/// inside a crop's More-details popup (<c>cropId</c> set). One query, because the two differ by a WHERE
/// clause and nothing else — a second endpoint would be a second place for the ordering and the paging to
/// drift.
/// <para>
/// PAGE AND PAGE SIZE ARE CLAMPED, not validated. This deviates from the admin logs pages, which 400 an
/// out-of-range pageSize, and the reason is who is asking: an admin typing a URL by hand is better served
/// by an error than by silently different data, whereas a farmer scrolling their sales on a phone must
/// never be shown a failure because a stale client asked for page 0. The clamp is documented on
/// <see cref="MaxPageSize"/> and pinned by tests.
/// </para>
/// </remarks>
public class GetSalesQuery : IRequest<Result<SalesPage_GetDto>>
{
    /// <summary>Smallest page size served; also what a caller asking for 0 or a negative number gets.</summary>
    public const int MinPageSize = 1;

    /// <summary>
    /// Largest page size served. Deliberately half the admin cap (100): these rows carry a farmer's own
    /// free-text notes, so the page a stray <c>?pageSize=1000</c> could pull out of the database is kept
    /// small on purpose.
    /// </summary>
    public const int MaxPageSize = 50;

    /// <summary>Default page size when the caller does not ask.</summary>
    public const int DefaultPageSize = 20;

    // Set from the JWT, never bound from the query string.
    public Guid UserId { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = DefaultPageSize;

    /// <summary>
    /// Optional crop filter for the per-crop popup list. An UNKNOWN crop id is not an error here — it is a
    /// filter that matches nothing, so the answer is an empty page. A read that 400s on a stale id would
    /// break a popup the moment a crop was renamed away underneath it.
    /// </summary>
    public Guid? CropId { get; set; }

    /// <summary>Page number as it will actually be used: never below 1.</summary>
    public int EffectivePage => Page < 1 ? 1 : Page;

    /// <summary>Page size as it will actually be used: clamped into [MinPageSize, MaxPageSize].</summary>
    public int EffectivePageSize => PageSize < MinPageSize
        ? MinPageSize
        : PageSize > MaxPageSize
            ? MaxPageSize
            : PageSize;
}
