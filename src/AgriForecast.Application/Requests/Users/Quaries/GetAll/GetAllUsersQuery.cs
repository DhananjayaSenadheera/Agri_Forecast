using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Users.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Users.Quaries.GetAll;

/// <summary>
/// Admin-only listing of users. Paging params are OPTIONAL with generous defaults: the frontend
/// admin console fetches once and paginates client-side (its consumer type is a flat
/// <c>AdminUser[]</c>), so with no params supplied a single generous page returns every user. The
/// handler clamps <see cref="Page"/> to >= 1 and <see cref="PageSize"/> to [1, 500].
/// </summary>
public class GetAllUsersQuery : IRequest<Result<List<AdminUserDto>>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 500;
}
