using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Crop.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Crop.Commands.Delete;

public class CropDeleteCommand : IRequest<Result<bool>>
{
    /// <summary>
    /// Admin-only deletion of a crop. <paramref name="actingUserId"/> is stamped by the
    /// controller from the JWT <c>sub</c> claim (never the body/route) so the audit trail can name
    /// who deleted the row; it is required rather than optional so a new call site cannot forget it.
    /// </summary>
    public CropDeleteCommand(Guid cropId, Guid actingUserId)
    {
        Id = cropId;
        ActingUserId = actingUserId;
    }

    public Guid Id { get; set; }

    /// <summary>The acting admin (JWT <c>sub</c>), recorded on the audit row.</summary>
    public Guid ActingUserId { get; }
}
