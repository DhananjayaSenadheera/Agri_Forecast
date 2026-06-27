using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Auth.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Auth.Commands.Login;

public class LoginCommand : IRequest<Result<AuthResponseDto>>
{
    public LoginDto LoginDto { get; set; }
}
