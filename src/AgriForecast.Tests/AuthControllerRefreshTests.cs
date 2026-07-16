using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using AgriForecast.API.Controllers;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Auth.Commands.Login;
using AgriForecast.Application.Requests.Auth.Commands.Refresh;
using AgriForecast.Application.Requests.Auth.Commands.Register;
using AgriForecast.Application.Requests.Auth.DTOs;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Entities;
using AgriForecast.Infrastructure.Security;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Unit tests for AuthController's refresh-cookie I/O. A DefaultHttpContext captures the Set-Cookie
/// header; the mediator and IRefreshTokenService are mocked, with the service producing REAL refresh
/// JWTs (via a real JwtTokenGenerator) so cookie-token assertions stay meaningful. The revocation
/// state machine itself is unit-tested in RefreshTokenServiceTests.
/// </summary>
public class AuthControllerRefreshTests
{
    private const string CookieName = "agriforecast_refresh";

    private static JwtSettings Settings() => new()
    {
        Key = "TestSuperSecretKey_MustBe16BytesMinimumOrMore!",
        Issuer = "AgriForecast.Tests",
        Audience = "AgriForecast.TestClient",
        AccessTokenMinutes = 60,
        RefreshTokenDays = 7
    };

    private static readonly Guid TestUserId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    // The controller derives the refresh-token subject from the access token, so the DTO must carry
    // a REAL access JWT whose subject is the user id.
    private static string AccessTokenFor(Guid id)
    {
        var gen = new JwtTokenGenerator(Options.Create(Settings()));
        var (tok, _) = gen.Generate(new User
        {
            Id = id,
            Username = "farmer_test",
            Email = "farmer@test.lk",
            Role = "Farmer",
            PasswordHash = "irrelevant",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        return tok;
    }

    private static AuthResponseDto Dto() => new()
    {
        AccessToken = AccessTokenFor(TestUserId),
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        Username = "farmer_test",
        Email = "farmer@test.lk",
        Role = "Farmer"
    };

    private static AuthController BuildController(
        Result<AuthResponseDto> mediatorResult,
        out DefaultHttpContext httpContext,
        string environment = "Development",
        string? requestCookieValue = null,
        bool rotationSucceeds = true)
    {
        var mediator = new Mock<IMediator>();
        mediator.Setup(m => m.Send(
                It.IsAny<IRequest<Result<AuthResponseDto>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(mediatorResult);

        var gen = new JwtTokenGenerator(Options.Create(Settings()));

        // Mock the revocation service. IssueNewFamilyAsync + a successful RotateAsync return REAL
        // refresh JWTs (bound to the requested user id) so cookie assertions remain faithful.
        var refresh = new Mock<IRefreshTokenService>();
        refresh.Setup(s => s.IssueNewFamilyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid uid, CancellationToken _) =>
            {
                var (tok, exp) = gen.GenerateRefreshToken(uid);
                return ((string?)tok, exp);
            });
        refresh.Setup(s => s.RotateAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rotationSucceeds
                ? RefreshRotationResult.Success(
                    TestUserId, gen.GenerateRefreshToken(TestUserId).token, DateTime.UtcNow.AddDays(7))
                : RefreshRotationResult.Fail());
        refresh.Setup(s => s.RevokeFamilyForTokenAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.EnvironmentName).Returns(environment);

        var controller = new AuthController(
            mediator.Object, refresh.Object, Options.Create(Settings()), env.Object);

        httpContext = new DefaultHttpContext();
        if (requestCookieValue is not null)
            httpContext.Request.Headers["Cookie"] = $"{CookieName}={requestCookieValue}";

        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    private static string SetCookieHeader(DefaultHttpContext ctx)
        => ctx.Response.Headers["Set-Cookie"].ToString();

    private static string? CookieTokenValue(string setCookie)
    {
        // agriforecast_refresh=<token>; path=...; ...
        var start = setCookie.IndexOf(CookieName + "=", StringComparison.Ordinal);
        if (start < 0) return null;
        start += CookieName.Length + 1;
        var end = setCookie.IndexOf(';', start);
        return end < 0 ? setCookie[start..] : setCookie[start..end];
    }

    // ── login / register issue the cookie with the contracted attributes ─────────────

    [Fact]
    public async Task Login_Success_SetsRefreshCookieWithContractedAttributes()
    {
        var controller = BuildController(Result<AuthResponseDto>.Success(Dto()), out var ctx,
            environment: "Production");

        var action = await controller.Login(new LoginCommand());

        Assert.IsType<OkObjectResult>(action);
        var rawSetCookie = SetCookieHeader(ctx);
        var setCookie = rawSetCookie.ToLowerInvariant();
        setCookie.Should().Contain($"{CookieName}=");
        setCookie.Should().Contain("httponly");
        setCookie.Should().Contain("samesite=strict");
        setCookie.Should().Contain("path=/api/auth");
        setCookie.Should().Contain("max-age=604800"); // 7 days in seconds
        setCookie.Should().Contain("secure", "Secure must be set outside Development");

        // Pin S1: the refresh cookie binds to the immutable user id and carries NO username.
        var refreshJwt = new JwtSecurityTokenHandler().ReadJwtToken(CookieTokenValue(rawSetCookie));
        refreshJwt.Subject.Should().Be(TestUserId.ToString());
        refreshJwt.Claims.Should().NotContain(c => c.Type == "unique_name" || c.Type == "name",
            "the refresh token must not embed the mutable username");
    }

    [Fact]
    public async Task Register_Success_SetsRefreshCookie()
    {
        var controller = BuildController(Result<AuthResponseDto>.Success(Dto()), out var ctx);

        var action = await controller.Register(new RegisterCommand());

        Assert.IsType<OkObjectResult>(action);
        SetCookieHeader(ctx).Should().Contain($"{CookieName}=");
    }

    [Fact]
    public async Task Login_InDevelopment_CookieIsNotSecure()
    {
        var controller = BuildController(Result<AuthResponseDto>.Success(Dto()), out var ctx,
            environment: "Development");

        await controller.Login(new LoginCommand());

        SetCookieHeader(ctx).ToLowerInvariant().Should()
            .NotContain("secure", "dev is served over http://localhost so Secure would break the cookie");
    }

    [Fact]
    public async Task Login_ResponseBody_IsUnchangedAuthResponseDto()
    {
        var dto = Dto();
        var controller = BuildController(Result<AuthResponseDto>.Success(dto), out _);

        var action = await controller.Login(new LoginCommand());

        var ok = Assert.IsType<OkObjectResult>(action);
        Assert.Same(dto, ok.Value); // response body must remain exactly AuthResponseDto
    }

    // ── refresh ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_Success_Returns200AndRotatesCookie()
    {
        var controller = BuildController(Result<AuthResponseDto>.Success(Dto()), out var ctx,
            environment: "Production", requestCookieValue: "old-refresh-token");

        var action = await controller.Refresh();

        Assert.IsType<OkObjectResult>(action);
        var newToken = CookieTokenValue(SetCookieHeader(ctx));
        newToken.Should().NotBeNullOrEmpty();
        newToken.Should().NotBe("old-refresh-token", "the cookie must be rotated on refresh");
    }

    [Fact]
    public async Task Refresh_Failure_Returns401AndClearsCookie()
    {
        // Rotation (store check) fails => 401 + cookie cleared, and the access-mint is never reached.
        var controller = BuildController(
            Result<AuthResponseDto>.Success(Dto()), out var ctx,
            requestCookieValue: "used-or-revoked-token", rotationSucceeds: false);

        var action = await controller.Refresh();

        var unauthorized = Assert.IsType<UnauthorizedObjectResult>(action);
        Assert.Equal(401, unauthorized.StatusCode);

        var setCookie = SetCookieHeader(ctx).ToLowerInvariant();
        setCookie.Should().Contain($"{CookieName}=");
        setCookie.Should().Contain("max-age=0", "a rejected refresh must clear the stale cookie");
    }

    [Fact]
    public async Task Logout_ClearsCookie_Returns204()
    {
        var controller = BuildController(Result<AuthResponseDto>.Success(Dto()), out var ctx,
            requestCookieValue: "some-refresh-token");

        var action = await controller.Logout();

        Assert.IsType<NoContentResult>(action);
        var setCookie = SetCookieHeader(ctx).ToLowerInvariant();
        setCookie.Should().Contain($"{CookieName}=");
        setCookie.Should().Contain("max-age=0");
        setCookie.Should().Contain("path=/api/auth");
    }

    // ── attribute wiring: AllowAnonymous + the "auth" rate-limit policy ───────────────

    [Fact]
    public void Controller_IsAnonymousAndAuthRateLimited()
    {
        var type = typeof(AuthController);

        // auth endpoints (incl. refresh/logout) are anonymous
        Assert.NotNull(type.GetCustomAttribute<AllowAnonymousAttribute>());

        var rateLimit = type.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(rateLimit);
        // refresh/logout inherit the class-level 'auth' policy
        Assert.Equal("auth", rateLimit!.PolicyName);
    }
}
