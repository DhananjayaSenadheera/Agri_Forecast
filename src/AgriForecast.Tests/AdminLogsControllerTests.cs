using AgriForecast.API.Controllers;
using AgriForecast.Application.Requests.Admin.Logs.Common;
using AgriForecast.Application.Requests.Admin.Logs.Queries.GetUserActivity;
using AgriForecast.Application.Services;
using AgriForecast.Domain.Enums;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// GET /api/admin/logs/user-activity at the CONTROLLER seam, exercised end-to-end through the real
/// GetUserActivityQueryHandler over a canned read store.
///
/// Why this exists on top of the handler tests: the FE sends the multi filter as ONE comma-joined,
/// URL-encoded value (<c>types=loginSucceeded%2CloginFailed</c>). ASP.NET Core decodes the %2C but
/// does NOT comma-split, so the parameter MUST be bound as a single <c>string?</c> and split by us —
/// binding it as <c>string[]</c> would silently produce one unsplittable element matching nothing and
/// hand the admin an empty page. These tests pin the binding shape and the split.
/// </summary>
public class AdminLogsControllerTests
{
    private sealed class CannedStore : ILogsReadStore
    {
        public List<(UserActivityRow Row, long Id)> Activity = new();
        public IReadOnlyCollection<UserActivityEventType>? CapturedTypes;

        public Task<UserActivityPage> GetUserActivityPageAsync(
            int page, int pageSize, IReadOnlyCollection<UserActivityEventType>? types,
            CancellationToken ct = default)
        {
            CapturedTypes = types;
            var filtered = Activity
                .Where(a => types is not { Count: > 0 } || types.Contains(a.Row.EventType))
                .OrderByDescending(a => a.Row.OccurredUtc).ThenByDescending(a => a.Id)
                .Select(a => a.Row)
                .ToList();
            return Task.FromResult(new UserActivityPage(
                filtered.Skip((page - 1) * pageSize).Take(pageSize).ToList(), filtered.Count));
        }

        public Task<TrainingRunsPage> GetTrainingRunsPageAsync(int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult(new TrainingRunsPage(new List<TrainingRunRow>(), 0));

        public Task<SystemErrorsPage> GetSystemErrorsAsync(int page, int pageSize, CancellationToken ct = default)
            => Task.FromResult(new SystemErrorsPage(new List<SystemErrorRow>(), 0));
    }

    // A mediator that routes GetUserActivityQuery to the REAL handler, so the controller's parameter
    // binding, the query, the handler's filter resolution and the store predicate are all in play.
    private static (AdminLogsController controller, CannedStore store) Build()
    {
        var store = new CannedStore();
        var now = DateTime.UtcNow;
        void Add(UserActivityEventType type, int minutesAgo, long id, string? details = null) =>
            store.Activity.Add((new UserActivityRow(
                now.AddMinutes(-minutesAgo), type, Guid.NewGuid(), null, null, details), id));

        Add(UserActivityEventType.LoginSucceeded, 1, 1);
        Add(UserActivityEventType.LoginFailed, 2, 2);
        Add(UserActivityEventType.PolicyFlagChanged, 3, 3, "created 'SUGAR-TAX'");
        Add(UserActivityEventType.FestivalChanged, 4, 4, "updated 'VESAK 2027-05-10'");
        Add(UserActivityEventType.CropChanged, 5, 5, "deleted 'VEG000071'");

        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<GetUserActivityQuery>(), It.IsAny<CancellationToken>()))
            .Returns((GetUserActivityQuery q, CancellationToken ct) =>
                new GetUserActivityQueryHandler(store).Handle(q, ct));

        return (new AdminLogsController(mediator.Object), store);
    }

    private static UserActivityPage_GetDto Payload(IActionResult result)
    {
        // Assert.IsType rather than FluentAssertions here: this project's FluentAssertions
        // Should() overload set does not resolve on IActionResult/object.
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<UserActivityPage_GetDto>(ok.Value);
    }

    [Fact]
    public async Task UserActivity_CommaJoinedTypes_FiltersToThatSet()
    {
        var (controller, store) = Build();

        // Exactly what arrives after ASP.NET URL-decodes types=policyFlagChanged%2CfestivalChanged.
        var result = await controller.GetUserActivity(types: "policyFlagChanged,festivalChanged");

        var page = Payload(result);
        page.Total.Should().Be(2);
        page.Items.Select(i => i.EventType).Should().BeEquivalentTo(new[]
        {
            UserActivityEventStrings.PolicyFlagChanged, UserActivityEventStrings.FestivalChanged
        });
        store.CapturedTypes.Should().BeEquivalentTo(new[]
        {
            UserActivityEventType.PolicyFlagChanged, UserActivityEventType.FestivalChanged
        });
    }

    [Fact]
    public async Task UserActivity_AllFiveContentTypes_InOneCommaJoinedValue()
    {
        var (controller, _) = Build();

        var result = await controller.GetUserActivity(types: string.Join(",", new[]
        {
            UserActivityEventStrings.PolicyFlagChanged,
            UserActivityEventStrings.FestivalChanged,
            UserActivityEventStrings.NewsEventChanged,
            UserActivityEventStrings.CropChanged,
            UserActivityEventStrings.MarketChanged
        }));

        // The three content rows in the canned set; the two login rows are excluded.
        Payload(result).Total.Should().Be(3);
    }

    [Fact]
    public async Task UserActivity_NoTypes_ReturnsEverything()
    {
        var (controller, store) = Build();

        var result = await controller.GetUserActivity();

        Payload(result).Total.Should().Be(5);
        store.CapturedTypes.Should().BeNull();
    }

    [Fact]
    public async Task UserActivity_SingleTypeParam_IsUnchanged()
    {
        var (controller, _) = Build();

        var result = await controller.GetUserActivity(type: "loginFailed");

        var page = Payload(result);
        page.Total.Should().Be(1);
        page.Items.Single().EventType.Should().Be(UserActivityEventStrings.LoginFailed);
    }

    [Fact]
    public async Task UserActivity_TypesWins_OverType()
    {
        var (controller, _) = Build();

        var result = await controller.GetUserActivity(type: "loginFailed", types: "cropChanged");

        Payload(result).Items.Single().EventType.Should().Be(UserActivityEventStrings.CropChanged);
    }

    // Binding shape guard: if someone "helpfully" changes the parameter to string[], a comma-joined
    // value would bind as ONE element and match nothing. Pin the signature.
    [Fact]
    public void UserActivity_TypesParameter_IsASingleString_NotAnArray()
    {
        var parameter = typeof(AdminLogsController)
            .GetMethod(nameof(AdminLogsController.GetUserActivity))!
            .GetParameters()
            .Single(p => p.Name == "types");

        parameter.ParameterType.Should().Be<string>(
            "ASP.NET Core does not comma-split a query value; the split is ours to do");
    }
}
