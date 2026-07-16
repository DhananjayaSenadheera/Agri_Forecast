using System.Reflection;
using AgriForecast.API.Controllers;
using AgriForecast.Domain.Constants;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;

namespace AgriForecast.Tests;

/// <summary>
/// API-9 — role-enforcement WIRING assertions. ASP.NET's authorization middleware returns 403 when
/// an authenticated caller lacks a required role and 401 when unauthenticated; these tests prove,
/// per endpoint, that the correct <c>[Authorize(Roles = "Admin")]</c> / <c>[Authorize]</c> /
/// <c>[AllowAnonymous]</c> attributes are actually present, so a non-admin is refused (403) on every
/// Admin-locked route and reads stay merely authenticated. (Attribute-level, so no TestServer/DB is
/// needed — deterministic and fast; a full HTTP 403 round-trip via WebApplicationFactory is a
/// recommended future add.)
/// </summary>
public class AdminAuthorizationWiringTests
{
    private static IEnumerable<AuthorizeAttribute> AuthAttrs(Type controller, string method)
    {
        var m = controller.GetMethod(method)
                ?? throw new MissingMethodException(controller.Name, method);
        return controller.GetCustomAttributes<AuthorizeAttribute>(true)
            .Concat(m.GetCustomAttributes<AuthorizeAttribute>(true));
    }

    private static bool IsAdminLocked(Type controller, string method) =>
        AuthAttrs(controller, method).Any(a =>
            (a.Roles ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(UserRoles.Admin));

    private static bool IsAnonymous(Type controller, string method) =>
        controller.GetCustomAttributes<AllowAnonymousAttribute>(true).Any()
        || controller.GetMethod(method)!.GetCustomAttributes<AllowAnonymousAttribute>(true).Any();

    private static bool RequiresAuthOnly(Type controller, string method) =>
        AuthAttrs(controller, method).Any() && !IsAnonymous(controller, method) && !IsAdminLocked(controller, method);

    // ── UserController: entirely Admin-only ────────────────────────────────────────
    [Theory]
    [InlineData(nameof(UserController.GetAll))]
    [InlineData(nameof(UserController.UpdateRole))]
    [InlineData(nameof(UserController.Delete))]
    public void UserController_AllActions_AreAdminOnly(string method)
    {
        IsAdminLocked(typeof(UserController), method).Should().BeTrue();
    }

    // ── Mutating endpoints on existing controllers: Admin-only ─────────────────────
    [Theory]
    [InlineData(typeof(CropController), nameof(CropController.CreateCrop))]
    [InlineData(typeof(CropController), nameof(CropController.Update))]
    [InlineData(typeof(CropController), nameof(CropController.DeleteCrop))]
    [InlineData(typeof(MarketController), nameof(MarketController.Create))]
    [InlineData(typeof(PolicyFlagController), nameof(PolicyFlagController.Create))]
    [InlineData(typeof(PolicyFlagController), nameof(PolicyFlagController.Update))]
    [InlineData(typeof(PolicyFlagController), nameof(PolicyFlagController.Delete))]
    public void MutatingEndpoints_AreAdminOnly(Type controller, string method)
    {
        IsAdminLocked(controller, method).Should().BeTrue(
            $"{controller.Name}.{method} mutates state and must be Admin-locked");
    }

    // ── Reads stay authenticated-only (NOT admin-gated) ────────────────────────────
    [Theory]
    [InlineData(typeof(CropController), nameof(CropController.GetAllCrops))]
    [InlineData(typeof(CropController), nameof(CropController.GetCropById))]
    [InlineData(typeof(MarketController), nameof(MarketController.GetAll))]
    [InlineData(typeof(PolicyFlagController), nameof(PolicyFlagController.GetAll))]
    [InlineData(typeof(ForecastController), nameof(ForecastController.GetMarketOverview))]
    [InlineData(typeof(ForecastController), nameof(ForecastController.GetBestCrops))]
    // API-11: economic-indicator + macro-series reads are authenticated-only (not Admin-gated) —
    // non-personal national reference data, no more privileged than market-overview.
    [InlineData(typeof(IndicatorsController), nameof(IndicatorsController.GetIndicators))]
    [InlineData(typeof(IndicatorsController), nameof(IndicatorsController.GetMacroSeries))]
    [InlineData(typeof(IndicatorsController), nameof(IndicatorsController.GetCatalog))]
    public void ReadEndpoints_RequireAuth_ButNotAdmin(Type controller, string method)
    {
        RequiresAuthOnly(controller, method).Should().BeTrue(
            $"{controller.Name}.{method} is a read: authenticated but not Admin-gated");
        IsAdminLocked(controller, method).Should().BeFalse();
    }

    // ── Auth endpoints stay anonymous by design ────────────────────────────────────
    [Theory]
    [InlineData(nameof(AuthController.Register))]
    [InlineData(nameof(AuthController.Login))]
    [InlineData(nameof(AuthController.Refresh))]
    [InlineData(nameof(AuthController.Logout))]
    public void AuthEndpoints_AreAnonymous(string method)
    {
        IsAnonymous(typeof(AuthController), method).Should().BeTrue();
        IsAdminLocked(typeof(AuthController), method).Should().BeFalse();
    }
}
