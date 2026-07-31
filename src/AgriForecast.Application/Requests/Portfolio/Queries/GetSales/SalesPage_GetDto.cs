using AgriForecast.Application.Requests.Portfolio.DTOs;

namespace AgriForecast.Application.Requests.Portfolio.Queries.GetSales;

// Response envelope for GET /api/portfolio/sales. An empty page is a 200 with an empty Items list, never a
// 404 — a farmer who has recorded nothing has an empty sales log, not a missing one.
//
// Identical shape to the admin logs pages (UserActivityPage_GetDto): { items, page, pageSize, total }. The
// portfolio area had no paging precedent of its own, so it borrows the one the FE already consumes rather
// than inventing a second envelope for the same idea.
//
// Page and PageSize are echoed back AS USED, i.e. after clamping — a client that asked for pageSize=1000
// and received 50 rows must be able to see that from the response rather than conclude it has them all.
public class SalesPage_GetDto
{
    public List<SaleItem_GetDto> Items { get; set; } = new();
    public int Page { get; set; }
    public int PageSize { get; set; }

    // Rows matching the (owner + optional crop) filter BEFORE paging — what the UI's page count is built
    // from.
    public int Total { get; set; }
}
