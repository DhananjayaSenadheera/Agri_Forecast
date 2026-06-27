using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Auth.DTOs;
using MediatR;

namespace AgriForecast.Application.Requests.Auth.Commands.Register;

public class RegisterCommand : IRequest<Result<AuthResponseDto>>
{
    public RegisterDto RegisterDto { get; set; }
}
