using AgriForecast.Application.common;
using AgriForecast.Application.Requests.NewsEvents.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.NewsEvents.Commands.Update;

// Returns the affected id on success. No mutation-result DTO / training-data warning: NewsEvents
// are capture-and-storage only and not yet ML inputs (deliberate divergence from API-10/13).
public class NewsEventUpdateCommand : IRequest<Result<Guid>>
{
    public NewsEvent_UpdateDto NewsEventUpdateDto { get; set; }

    /// <summary>
    /// The acting admin, stamped by the controller from the JWT <c>sub</c> claim. Any value supplied
    /// in the request body is OVERWRITTEN — it exists on the command only so the audit trail can name
    /// who made the change; it can never be forged by a caller.
    /// </summary>
    public Guid ActingUserId { get; set; }
}
