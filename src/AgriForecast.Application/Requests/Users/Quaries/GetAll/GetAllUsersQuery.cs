using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Users.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Users.Quaries.GetAll;

/// <summary>
/// Admin-only listing of users. Paging is optional with generous defaults, because the admin console
/// fetches once and paginates client-side. The handler clamps Page to >= 1 and PageSize to [1, 500].
/// </summary>
public class GetAllUsersQuery : IRequest<Result<List<AdminUserDto>>>
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 500;
}
