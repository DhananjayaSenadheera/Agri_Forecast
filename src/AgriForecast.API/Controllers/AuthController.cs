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
    IRefreshTokenService refreshTokenService,
    IOptions<JwtSettings> jwtSettings,
    IWebHostEnvironment environment) : ControllerBase
{
    // Refresh token: a signed JWT carried in this HttpOnly cookie, now backed by a PERSISTED
    // revocation store (RefreshTokenRecord, keyed by jti and chained by family). A refresh is
    // honoured only when the token is crypto-valid AND its jti row exists, is unexpired, not
    // revoked, and not already used; rotation marks the old row used and issues a chained child.
    // Reuse of a used token revokes the whole family; logout and admin delete/demote revoke too.
    // Fail-closed: a store error rejects the refresh (see IRefreshTokenService). Cookie semantics
    // (HttpOnly + SameSite=Strict + Secure outside dev + path=/api/auth + rotation) are unchanged.
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
            await IssueRefreshCookie(result.Data);
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
            await IssueRefreshCookie(result.Data);
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

        // Rotate against the persisted store first: crypto-valid AND jti row current (unexpired, not
        // revoked, not used). Reuse-detection revokes the family; a store error fails closed. This
        // returns the brand-new child refresh token to set as the rotated cookie.
        var rotation = await refreshTokenService.RotateAsync(refreshToken);
        if (!rotation.IsSuccess)
        {
            ClearRefreshCookie();
            return Unauthorized(ToErrorResponse("Invalid or expired refresh token."));
        }

        // Re-mint the access token (re-resolves username/email/role from the store) via the handler.
        var result = await mediator.Send(new RefreshCommand { RefreshToken = refreshToken });
        if (!result.IsSuccess)
        {
            ClearRefreshCookie();
            return Unauthorized(ToErrorResponse(result.Error));
        }

        // Rotate the cookie to the freshly-issued child refresh token.
        SetRefreshCookie(rotation.Token!);
        return Ok(result.Data);
    }

    /// <summary>
    /// Revokes the presented refresh token's whole family server-side, then clears the cookie so a
    /// stolen copy of the token is dead even though the browser had already discarded it.
    /// </summary>
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var refreshToken = Request.Cookies[RefreshCookieName];
        await refreshTokenService.RevokeFamilyForTokenAsync(refreshToken);
        ClearRefreshCookie();
        return NoContent();
    }

    private async Task IssueRefreshCookie(AuthResponseDto dto)
    {
        // Bind the refresh token to the IMMUTABLE user id, not the mutable username, so a stale
        // refresh token can never resolve to a different account after a rename/re-register.
        // AuthResponseDto (the locked body) carries no id, but the access token just minted for it
        // does — its subject is user.Id — so read it back as the single source of truth. The service
        // mints the token in a new family and persists its record; a null token means persistence
        // failed, in which case we omit the cookie (fail-safe — no unrevocable token in the wild).
        var userId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(dto.AccessToken).Subject);
        var (refreshToken, _) = await refreshTokenService.IssueNewFamilyAsync(userId);
        if (refreshToken is not null)
            SetRefreshCookie(refreshToken);
    }

    private void SetRefreshCookie(string refreshToken)
    {
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
