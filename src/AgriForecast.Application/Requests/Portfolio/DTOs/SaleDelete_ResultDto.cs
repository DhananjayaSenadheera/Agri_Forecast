namespace AgriForecast.Application.Requests.Portfolio.DTOs;

// The result of DELETE /api/portfolio/sales/{id}.
//
// NOT A RESPONSE BODY: the endpoint answers 204 No Content, because there is nothing left to describe. This
// type exists so the command can be an IRequest<Result<T>> like every other command in the area (the house
// Result<T> has no non-generic form) and so the handler can hand the controller the id it removed for
// logging. Precedent: WatchlistRemove_ResultDto, which IS returned — the difference is the status code the
// owner chose for each, not a different idea of what a delete means.
public class SaleDelete_ResultDto
{
    public Guid SaleId { get; set; }

    public bool Removed { get; set; }
}
