using AgriForecast.Application.Requests.Auth.Commands.Login;
using AgriForecast.Application.Requests.Auth.Commands.Refresh;
using AgriForecast.Application.Requests.Auth.Commands.Register;
using AgriForecast.Application.Requests.Auth.DTOs;
using AgriForecast.Application.Services;
using System.IdentityModel.Tokens.Jwt;
using AgriForecast.Infrastructure.Security;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AgriForecast.API.Controllers;

[ApiController]
[Route("api/auth")]
[AllowAnonymous]
// Stricter rate limit on auth endpoints to blunt credential-stuffing / brute force (F-08).
// This class-level policy also covers /refresh and /logout below.
[EnableRateLimiting("auth")]
public class AuthController(
    IMediator mediator,
    IJwtTokenGenerator jwtTokenGenerator,
    IOptions<JwtSettings> jwtSettings,
    IWebHostEnvironment environment) : ControllerBase
{
    // Stateless refresh token: a signed JWT carried in this HttpOnly cookie. There is NO
    // server-side store, so a leaked/stolen refresh token cannot be individually revoked before
    // it expires (7 days). POST-HOLD UPGRADE PATH: add a persisted token/family id (jti) table or
    // a per-user token version claim and check/rotate it on refresh to enable revocation and
    // reuse-detection. Until then, mitigations are: short-ish lifetime, HttpOnly + SameSite=Strict
    // + Secure (outside dev), path-scoped to /api/auth, and rotation on every refresh.
    private const string RefreshCookieName = "agriforecast_refresh";
    private const string RefreshCookiePath = "/api/auth";

    private static object ToErrorResponse(string error) => new
    {
        errors = new[]
        {
            new { property = "Auth", message = error }
        }
    };

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterCommand command)
    {
        var result = await mediator.Send(command);
        if (result.IsSuccess)
        {
            IssueRefreshCookie(result.Data);
            return Ok(result.Data);
        }

        return BadRequest(ToErrorResponse(result.Error));
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginCommand command)
    {
        var result = await mediator.Send(command);
        if (result.IsSuccess)
        {
            IssueRefreshCookie(result.Data);
            return Ok(result.Data);
        }

        return Unauthorized(ToErrorResponse(result.Error));
    }

    /// <summary>
    /// Exchanges the refresh cookie for a fresh access token and rotates the cookie.
    /// No request body — the refresh JWT is read from the HttpOnly cookie.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        var refreshToken = Request.Cookies[RefreshCookieName];
        var result = await mediator.Send(new RefreshCommand { RefreshToken = refreshToken });
        if (result.IsSuccess)
        {
            // Rotate: every successful refresh issues a brand-new 7-day refresh cookie.
            IssueRefreshCookie(result.Data);
            return Ok(result.Data);
        }

        // A stale/invalid cookie is worthless — clear it so the browser stops resending it.
        ClearRefreshCookie();
        return Unauthorized(ToErrorResponse(result.Error));
    }

    /// <summary>Clears the refresh cookie. Stateless — nothing to revoke server-side.</summary>
    [HttpPost("logout")]
    public IActionResult Logout()
    {
        ClearRefreshCookie();
        return NoContent();
    }

    private void IssueRefreshCookie(AuthResponseDto dto)
    {
        // Bind the refresh token to the IMMUTABLE user id, not the mutable username, so a stale
        // 7-day refresh token can never resolve to a different account after a rename/re-register.
        // AuthResponseDto (the locked body) carries no id, but the access token just minted for it
        // does — its subject is user.Id — so read it back as the single source of truth.
        var userId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(dto.AccessToken).Subject);
        var (refreshToken, _) = jwtTokenGenerator.GenerateRefreshToken(userId);
        Response.Cookies.Append(RefreshCookieName, refreshToken, BuildCookieOptions(
            TimeSpan.FromDays(jwtSettings.Value.RefreshTokenDays)));
    }

    private void ClearRefreshCookie()
    {
        // Empty value + already-expired => browser drops the cookie. Attributes must match the
        // ones used when setting it (Path/SameSite/Secure) for the clear to take effect.
        Response.Cookies.Append(RefreshCookieName, string.Empty, BuildCookieOptions(TimeSpan.Zero));
    }

    private CookieOptions BuildCookieOptions(TimeSpan maxAge) => new()
    {
        HttpOnly = true,
        // Secure everywhere EXCEPT Development, where the app is served over plain http://localhost.
        Secure = !environment.IsDevelopment(),
        SameSite = SameSiteMode.Strict,
        Path = RefreshCookiePath,
        MaxAge = maxAge,
        Expires = maxAge == TimeSpan.Zero
            ? DateTimeOffset.UnixEpoch
            : DateTimeOffset.UtcNow.Add(maxAge)
    };
}
