using System.Reflection;
using AgriForecast.API.Controllers;
using AgriForecast.Domain.Constants;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;

namespace AgriForecast.Tests;

/// <summary>
/// Role-enforcement WIRING assertions: per endpoint, prove the correct [Authorize(Roles = "Admin")] /
/// [Authorize] / [AllowAnonymous] attribute is actually present, so a non-admin is refused on every
/// Admin-locked route and reads stay merely authenticated. Attribute-level, so no TestServer or DB.
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

    // UserController: entirely Admin-only.
    [Theory]
    [InlineData(nameof(UserController.GetAll))]
    [InlineData(nameof(UserController.UpdateRole))]
    [InlineData(nameof(UserController.Delete))]
    public void UserController_AllActions_AreAdminOnly(string method)
    {
        IsAdminLocked(typeof(UserController), method).Should().BeTrue();
    }

    // Mutating endpoints on existing controllers: Admin-only.
    [Theory]
    [InlineData(typeof(CropController), nameof(CropController.CreateCrop))]
    [InlineData(typeof(CropController), nameof(CropController.Update))]
    [InlineData(typeof(CropController), nameof(CropController.DeleteCrop))]
    [InlineData(typeof(MarketController), nameof(MarketController.Create))]
    [InlineData(typeof(PolicyFlagController), nameof(PolicyFlagController.Create))]
    [InlineData(typeof(PolicyFlagController), nameof(PolicyFlagController.Update))]
    [InlineData(typeof(PolicyFlagController), nameof(PolicyFlagController.Delete))]
    [InlineData(typeof(FestivalCalendarController), nameof(FestivalCalendarController.Create))]
    [InlineData(typeof(FestivalCalendarController), nameof(FestivalCalendarController.Update))]
    [InlineData(typeof(FestivalCalendarController), nameof(FestivalCalendarController.Delete))]
    // API-12: news-event mutations are Admin-only (curated data).
    [InlineData(typeof(NewsEventController), nameof(NewsEventController.Create))]
    [InlineData(typeof(NewsEventController), nameof(NewsEventController.Update))]
    [InlineData(typeof(NewsEventController), nameof(NewsEventController.Delete))]
    public void MutatingEndpoints_AreAdminOnly(Type controller, string method)
    {
        IsAdminLocked(controller, method).Should().BeTrue(
            $"{controller.Name}.{method} mutates state and must be Admin-locked");
    }

    // Reads stay authenticated-only, not admin-gated.
    [Theory]
    [InlineData(typeof(CropController), nameof(CropController.GetAllCrops))]
    [InlineData(typeof(CropController), nameof(CropController.GetCropById))]
    [InlineData(typeof(MarketController), nameof(MarketController.GetAll))]
    [InlineData(typeof(PolicyFlagController), nameof(PolicyFlagController.GetAll))]
    [InlineData(typeof(FestivalCalendarController), nameof(FestivalCalendarController.GetAll))]
    [InlineData(typeof(ForecastController), nameof(ForecastController.GetMarketOverview))]
    [InlineData(typeof(ForecastController), nameof(ForecastController.GetBestCrops))]
    // API-11: economic-indicator + macro-series reads are authenticated-only (not Admin-gated) —
    // non-personal national reference data, no more privileged than market-overview.
    [InlineData(typeof(IndicatorsController), nameof(IndicatorsController.GetIndicators))]
    [InlineData(typeof(IndicatorsController), nameof(IndicatorsController.GetMacroSeries))]
    [InlineData(typeof(IndicatorsController), nameof(IndicatorsController.GetCatalog))]
    // API-12: news-event list is authenticated-only (not Admin-gated) — non-personal reference data.
    [InlineData(typeof(NewsEventController), nameof(NewsEventController.GetAll))]
    public void ReadEndpoints_RequireAuth_ButNotAdmin(Type controller, string method)
    {
        RequiresAuthOnly(controller, method).Should().BeTrue(
            $"{controller.Name}.{method} is a read: authenticated but not Admin-gated");
        IsAdminLocked(controller, method).Should().BeFalse();
    }

    // AdminIngestionController: the two reads are Admin-only. Unlike the other read endpoints they surface
    // operational internals, so prove [Authorize(Roles = Admin)] is actually wired on each action.
    [Theory]
    [InlineData(nameof(AdminIngestionController.GetStatus))]
    [InlineData(nameof(AdminIngestionController.GetRuns))]
    public void AdminIngestionReads_AreAdminOnly(string method)
    {
        IsAdminLocked(typeof(AdminIngestionController), method).Should().BeTrue(
            $"AdminIngestionController.{method} exposes ingestion internals and must be Admin-locked");
        IsAnonymous(typeof(AdminIngestionController), method).Should().BeFalse();
    }

    // The service-control POSTs run (or halt) the entire data pipeline, so they are the highest-privilege
    // endpoints on the controller. A bare [Authorize] here would make ingestion farmer-triggerable.
    [Theory]
    [InlineData(nameof(AdminIngestionController.StartService))]
    [InlineData(nameof(AdminIngestionController.StopService))]
    public void IngestionServiceControls_AreAdminOnly(string method)
    {
        IsAdminLocked(typeof(AdminIngestionController), method).Should().BeTrue(
            $"AdminIngestionController.{method} runs the data pipeline and must be Admin-locked");
        IsAnonymous(typeof(AdminIngestionController), method).Should().BeFalse();
    }

    // AdminLogsController: both reads are Admin-only for the same reason (model-promotion decisions and
    // security events). Prove [Authorize(Roles = Admin)] is actually wired on each action.
    [Theory]
    [InlineData(nameof(AdminLogsController.GetTraining))]
    [InlineData(nameof(AdminLogsController.GetUserActivity))]
    public void AdminLogsReads_AreAdminOnly(string method)
    {
        IsAdminLocked(typeof(AdminLogsController), method).Should().BeTrue(
            $"AdminLogsController.{method} exposes Logs-hub internals and must be Admin-locked");
        IsAnonymous(typeof(AdminLogsController), method).Should().BeFalse();
    }

    // AdminForecastAccuracyController: both reads are Admin-only. They expose how the model has actually
    // scored — model-vs-fallback skill and per-version performance — which is operator information. A
    // bare [Authorize] would put it in front of farmers, who are shown a forecast, not its error record.
    [Theory]
    [InlineData(nameof(AdminForecastAccuracyController.GetSummary))]
    [InlineData(nameof(AdminForecastAccuracyController.GetSnapshots))]
    public void AdminForecastAccuracyReads_AreAdminOnly(string method)
    {
        IsAdminLocked(typeof(AdminForecastAccuracyController), method).Should().BeTrue(
            $"AdminForecastAccuracyController.{method} exposes model-accuracy internals and must be Admin-locked");
        IsAnonymous(typeof(AdminForecastAccuracyController), method).Should().BeFalse();
    }

    // PortfolioController: every action is authenticated but NOT Admin-locked — this is each farmer's own
    // surface, the exact opposite of the accuracy controller above. Admin-locking it would lock every
    // farmer out of their own watchlist; leaving an action anonymous would expose personal data to anyone,
    // because the owner is resolved from the JWT subject and an anonymous request has no subject to scope
    // to. Both mistakes are one attribute away, so each action is asserted individually.
    [Theory]
    [InlineData(nameof(PortfolioController.GetWatchlist))]
    [InlineData(nameof(PortfolioController.AddToWatchlist))]
    [InlineData(nameof(PortfolioController.UpdateWatchlistMarket))]
    [InlineData(nameof(PortfolioController.RemoveFromWatchlist))]
    [InlineData(nameof(PortfolioController.GetDashboard))]
    public void PortfolioEndpoints_RequireAuth_ButNotAdmin(string method)
    {
        RequiresAuthOnly(typeof(PortfolioController), method).Should().BeTrue(
            $"PortfolioController.{method} is a farmer's own data: authenticated, not Admin-gated");
        IsAnonymous(typeof(PortfolioController), method).Should().BeFalse(
            $"PortfolioController.{method} resolves its owner from the JWT, so anonymous access has no owner");
        IsAdminLocked(typeof(PortfolioController), method).Should().BeFalse();
    }

    // Auth endpoints stay anonymous by design.
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
