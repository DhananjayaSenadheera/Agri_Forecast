using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Auth.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Auth.Commands.Refresh;

/// <summary>
/// Exchanges a valid refresh JWT (read from the HttpOnly cookie by the controller) for a fresh
/// access token. The raw cookie value is passed in; the handler validates it and re-resolves the
/// user. The controller is responsible for reading the cookie and rotating it on success.
/// </summary>
public class RefreshCommand : IRequest<Result<AuthResponseDto>>
{
    public string? RefreshToken { get; set; }
}
