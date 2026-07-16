using AgriForecast.Application.common;
using AgriForecast.Application.Mapper;
using AgriForecast.Application.Requests.Users.DTOs;
using AgriForecast.Domain.Interfaces;
using MediatR;

namespace AgriForecast.Application.Requests.Users.Quaries.GetAll;

/// <summary>
/// Returns a page of users (newest first) projected to <see cref="AdminUserDto"/> (no PasswordHash).
/// Paging is clamped server-side so bad/hostile params can never over-fetch or under-fetch:
/// Page >= 1, PageSize in [1, 500]. An empty store returns an empty list (success), never a failure.
/// </summary>
public class GetAllUsersQueryHandler : IRequestHandler<GetAllUsersQuery, Result<List<AdminUserDto>>>
{
    private const int MaxPageSize = 500;
    private readonly IUserRepository _userRepository;

    public GetAllUsersQueryHandler(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    public async Task<Result<List<AdminUserDto>>> Handle(GetAllUsersQuery request, CancellationToken cancellationToken)
    {
        var page = request.Page < 1 ? 1 : request.Page;
        var pageSize = request.PageSize < 1 ? 1
            : request.PageSize > MaxPageSize ? MaxPageSize
            : request.PageSize;

        var users = await _userRepository.GetAllAsync();

        var pageItems = users
            .OrderByDescending(u => u.CreatedAt)
            .ThenBy(u => u.Username)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => u.ToAdminDto())
            .ToList();

        return Result<List<AdminUserDto>>.Success(pageItems);
    }
}
