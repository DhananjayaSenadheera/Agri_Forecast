using System.Security.Claims;
using AgriForecast.API.Controllers;
using AgriForecast.Application.common;
using AgriForecast.Application.Requests.Portfolio.Commands.AddWatchlistCrop;
using AgriForecast.Application.Requests.Portfolio.Commands.RemoveWatchlistCrop;
using AgriForecast.Application.Requests.Portfolio.Commands.UpdateWatchlistEntry;
using AgriForecast.Application.Requests.Portfolio.Common;
using AgriForecast.Application.Requests.Portfolio.DTOs;
using AgriForecast.Application.Requests.Portfolio.Queries.GetDashboard;
using AgriForecast.Application.Requests.Portfolio.Queries.GetWatchlist;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace AgriForecast.Tests;

/// <summary>
/// Wiring tests for PortfolioController: where the caller's identity comes from, and how a handler's
/// error code becomes an HTTP status.
/// <para>
/// The identity tests are the point. Every action stamps the user from the JWT subject, so a request body
/// or route that names a different user must be ignored rather than trusted — that is the mechanism the
/// whole cross-user isolation story rests on, and it lives in the controller, not the handler.
/// </para>
/// Mirrors the AdminIngestionController harness (DefaultHttpContext + a mocked IMediator).
/// </summary>
public class PortfolioControllerTests
{
    private static readonly Guid Caller = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Victim = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RouteCrop = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid BodyCrop = Guid.Parse("c0000000-0000-0000-0000-000000000002");

    private static DefaultHttpContext HttpContextFor(Guid userId)
    {
        var ctx = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) }, "TestAuth"))
        };
        return ctx;
    }

    private static DefaultHttpContext HttpContextWithoutSubject() => new();

    private static PortfolioController ControllerFor(IMediator mediator, HttpContext ctx)
        => new(mediator) { ControllerContext = new ControllerContext { HttpContext = ctx } };

    private static Mock<IMediator> MediatorReturning<TRequest, TResponse>(TResponse response)
        where TRequest : IRequest<TResponse>
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<TRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);
        return mediator;
    }

    // Identity: the JWT is the only source.

    [Fact]
    public async Task Add_StampsTheUserFromTheJwt_IgnoringAnyUserIdInTheBody()
    {
        AddWatchlistCropCommand? captured = null;
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<AddWatchlistCropCommand>(), It.IsAny<CancellationToken>()))
            .Callback((object c, CancellationToken _) => captured = (AddWatchlistCropCommand)c)
            .ReturnsAsync(Result<WatchlistAdd_ResultDto>.Success(new WatchlistAdd_ResultDto()));

        var controller = ControllerFor(mediator.Object, HttpContextFor(Caller));

        // A hostile body naming another user.
        await controller.AddToWatchlist(new AddWatchlistCropCommand
        {
            UserId = Victim,
            CropId = BodyCrop
        });

        captured!.UserId.Should().Be(Caller,
            "the owner comes from the JWT subject; a body value can never redirect the write");
    }

    [Fact]
    public async Task Update_TakesTheCropFromTheRoute_AndTheUserFromTheJwt()
    {
        UpdateWatchlistEntryCommand? captured = null;
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<UpdateWatchlistEntryCommand>(), It.IsAny<CancellationToken>()))
            .Callback((object c, CancellationToken _) => captured = (UpdateWatchlistEntryCommand)c)
            .ReturnsAsync(Result<WatchlistEntryUpdate_ResultDto>.Success(
                new WatchlistEntryUpdate_ResultDto()));

        var controller = ControllerFor(mediator.Object, HttpContextFor(Caller));

        await controller.UpdateWatchlistEntry(RouteCrop, new UpdateWatchlistEntryCommand
        {
            UserId = Victim,
            CropId = BodyCrop
        });

        captured!.UserId.Should().Be(Caller);
        captured.CropId.Should().Be(RouteCrop,
            "the route is the authority for which crop, so a mismatched body cannot redirect the write");
    }

    [Fact]
    public async Task Remove_PassesTheJwtUserAndRouteCrop()
    {
        RemoveWatchlistCropCommand? captured = null;
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<RemoveWatchlistCropCommand>(), It.IsAny<CancellationToken>()))
            .Callback((object c, CancellationToken _) => captured = (RemoveWatchlistCropCommand)c)
            .ReturnsAsync(Result<WatchlistRemove_ResultDto>.Success(new WatchlistRemove_ResultDto()));

        await ControllerFor(mediator.Object, HttpContextFor(Caller)).RemoveFromWatchlist(RouteCrop);

        captured!.UserId.Should().Be(Caller);
        captured.CropId.Should().Be(RouteCrop);
    }

    [Fact]
    public async Task Reads_ScopeToTheJwtUser()
    {
        GetWatchlistQuery? watchlist = null;
        GetPortfolioDashboardQuery? dashboard = null;

        var mediator = new Mock<IMediator>();
        mediator
            .Setup(m => m.Send(It.IsAny<GetWatchlistQuery>(), It.IsAny<CancellationToken>()))
            .Callback((object q, CancellationToken _) => watchlist = (GetWatchlistQuery)q)
            .ReturnsAsync(Result<List<WatchlistItem_GetDto>>.Success(new List<WatchlistItem_GetDto>()));
        mediator
            .Setup(m => m.Send(It.IsAny<GetPortfolioDashboardQuery>(), It.IsAny<CancellationToken>()))
            .Callback((object q, CancellationToken _) => dashboard = (GetPortfolioDashboardQuery)q)
            .ReturnsAsync(Result<PortfolioDashboard_GetDto>.Success(new PortfolioDashboard_GetDto()));

        var controller = ControllerFor(mediator.Object, HttpContextFor(Caller));
        await controller.GetWatchlist();
        await controller.GetDashboard();

        watchlist!.UserId.Should().Be(Caller);
        dashboard!.UserId.Should().Be(Caller);
    }

    // A token with no usable subject is a 401, never a guess at who is calling.

    [Fact]
    public async Task EveryAction_WithoutASubjectClaim_Is401()
    {
        var mediator = new Mock<IMediator>();
        var controller = ControllerFor(mediator.Object, HttpContextWithoutSubject());

        // Assert.IsType rather than Should().BeOfType(): FluentAssertions 8 binds a bare IActionResult to
        // its enum overload, and this is the house style for controller results anyway.
        Assert.IsType<UnauthorizedObjectResult>(await controller.GetWatchlist());
        Assert.IsType<UnauthorizedObjectResult>(await controller.GetDashboard());
        Assert.IsType<UnauthorizedObjectResult>(
            await controller.AddToWatchlist(new AddWatchlistCropCommand()));
        Assert.IsType<UnauthorizedObjectResult>(
            await controller.UpdateWatchlistEntry(RouteCrop, new UpdateWatchlistEntryCommand()));
        Assert.IsType<UnauthorizedObjectResult>(await controller.RemoveFromWatchlist(RouteCrop));

        mediator.Verify(
            m => m.Send(It.IsAny<IRequest<object>>(), It.IsAny<CancellationToken>()), Times.Never,
            "an unidentifiable caller never reaches a handler");
    }

    // Error mapping.

    [Fact]
    public async Task Update_NotFoundCode_Maps404_WithTheMachineReadableBody()
    {
        var mediator = MediatorReturning<UpdateWatchlistEntryCommand, Result<WatchlistEntryUpdate_ResultDto>>(
            Result<WatchlistEntryUpdate_ResultDto>.Failure(PortfolioErrors.WatchlistEntryNotFound));

        var response = await ControllerFor(mediator.Object, HttpContextFor(Caller))
            .UpdateWatchlistEntry(RouteCrop, new UpdateWatchlistEntryCommand());

        var notFound = Assert.IsType<NotFoundObjectResult>(response);
        notFound.StatusCode.Should().Be(StatusCodes.Status404NotFound,
            "404, not 403 — a 403 would confirm that the row exists for somebody else");
        // The body is the machine-readable { "error": "<code>" } shape, deliberately different from the
        // { errors: [{ property, message }] } used for validation failures.
        var code = notFound.Value!.GetType().GetProperty("error")!.GetValue(notFound.Value) as string;
        code.Should().Be(PortfolioErrors.WatchlistEntryNotFound);
    }

    [Fact]
    public async Task Remove_NotFoundCode_Maps404()
    {
        var mediator = MediatorReturning<RemoveWatchlistCropCommand, Result<WatchlistRemove_ResultDto>>(
            Result<WatchlistRemove_ResultDto>.Failure(PortfolioErrors.WatchlistEntryNotFound));

        var response = await ControllerFor(mediator.Object, HttpContextFor(Caller))
            .RemoveFromWatchlist(RouteCrop);

        Assert.IsType<NotFoundObjectResult>(response);
    }

    [Fact]
    public async Task AnyOtherFailure_StaysA400_InTheValidationErrorShape()
    {
        var mediator = MediatorReturning<RemoveWatchlistCropCommand, Result<WatchlistRemove_ResultDto>>(
            Result<WatchlistRemove_ResultDto>.Failure("something else went wrong"));

        var response = await ControllerFor(mediator.Object, HttpContextFor(Caller))
            .RemoveFromWatchlist(RouteCrop);

        // Only the pinned not-found codes become 404s; everything else keeps the usual 400 shape.
        Assert.IsType<BadRequestObjectResult>(response);
    }

    [Theory]
    [InlineData("watchlist_full")]
    [InlineData("too_many_markets")]
    [InlineData("invalid_planted_date")]
    public async Task Update_UnprocessableCode_Maps422_WithTheMachineReadableBody(string code)
    {
        var mediator = MediatorReturning<UpdateWatchlistEntryCommand, Result<WatchlistEntryUpdate_ResultDto>>(
            Result<WatchlistEntryUpdate_ResultDto>.Failure(code));

        var response = await ControllerFor(mediator.Object, HttpContextFor(Caller))
            .UpdateWatchlistEntry(RouteCrop, new UpdateWatchlistEntryCommand());

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(response);
        unprocessable.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity,
            "a well-formed request the product refuses is a 422; a 400 would tell the UI the payload was "
            + "malformed and send a developer hunting a serialization bug that is not there");
        var body = unprocessable.Value!.GetType().GetProperty("error")!.GetValue(unprocessable.Value) as string;
        body.Should().Be(code);
    }

    [Fact]
    public async Task Add_CapCodes_Map422()
    {
        var mediator = MediatorReturning<AddWatchlistCropCommand, Result<WatchlistAdd_ResultDto>>(
            Result<WatchlistAdd_ResultDto>.Failure(PortfolioErrors.WatchlistFull));

        var response = await ControllerFor(mediator.Object, HttpContextFor(Caller))
            .AddToWatchlist(new AddWatchlistCropCommand());

        Assert.IsType<UnprocessableEntityObjectResult>(response);
    }

    [Theory]
    [InlineData("clear_reason_required")]
    [InlineData("clear_reason_not_applicable")]
    [InlineData("invalid_clear_reason")]
    [InlineData("clear_reason_note_without_reason")]
    [InlineData("clear_reason_note_too_long")]
    public async Task Update_ClearReasonCode_Maps400_WithTheMachineReadableBody(string code)
    {
        var mediator = MediatorReturning<UpdateWatchlistEntryCommand, Result<WatchlistEntryUpdate_ResultDto>>(
            Result<WatchlistEntryUpdate_ResultDto>.Failure(code));

        var response = await ControllerFor(mediator.Object, HttpContextFor(Caller))
            .UpdateWatchlistEntry(RouteCrop, new UpdateWatchlistEntryCommand());

        var badRequest = Assert.IsType<BadRequestObjectResult>(response);
        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest,
            "the payload really is wrong — a reason is missing, contradictory, misspelled or over-long");

        // The CODE body, not the prose validation shape: the UI has to react to each of these differently,
        // and switching on a code beats parsing a sentence.
        var body = badRequest.Value!.GetType().GetProperty("error")!.GetValue(badRequest.Value) as string;
        body.Should().Be(code);
    }

    [Fact]
    public void NotFoundCodes_AreMatchedExactly_NotLoosely()
    {
        PortfolioErrors.IsNotFound(PortfolioErrors.WatchlistEntryNotFound).Should().BeTrue();
        PortfolioErrors.IsNotFound("Watchlist_Entry_Not_Found").Should().BeFalse(
            "these are wire values the UI switches on, so the match is exact and case-sensitive");
        PortfolioErrors.IsNotFound(null).Should().BeFalse();
        PortfolioErrors.IsNotFound("watchlist entry not found").Should().BeFalse();
    }
}
